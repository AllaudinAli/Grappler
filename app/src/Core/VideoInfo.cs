using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace Grappler.Core
{
    /// <summary>One entry in a fetched video's quality list.</summary>
    public sealed class FormatChoice
    {
        public const string KindVideo = "video";
        public const string KindAudioMp3 = "mp3";
        public const string KindAudio = "audio";
        public const string KindSubs = "subs";
        public const string KindPicker = "picker";

        public string Label { get; private set; }
        public string Detail { get; private set; }
        public string[] Args { get; private set; }
        public string Kind { get; private set; }

        /// <summary>For video choices: the height it caps at (0 = no cap).</summary>
        public int Height { get; private set; }

        /// <summary>Opens the full format list instead of downloading straight away.</summary>
        public bool OpensPicker { get { return Kind == KindPicker; } }

        public FormatChoice(string label, string detail, string kind, int height, string[] args)
        {
            Label = label;
            Detail = detail ?? "";
            Kind = kind;
            Height = height;
            Args = args ?? new string[0];
        }

        public override string ToString()
        {
            return Detail.Length == 0 ? Label : Label + "   ·   " + Detail;
        }
    }

    /// <summary>
    /// What a link turned out to be, read from the engine's full JSON
    /// dump: the details shown on the row, and the quality choices built
    /// from the formats the site really offers - so nobody is offered 1080p
    /// for a 480p video, and sizes are known before anything downloads.
    /// </summary>
    public sealed class VideoInfo
    {
        public string Title { get; private set; }
        public string Uploader { get; private set; }
        public string Duration { get; private set; }
        public string ThumbnailUrl { get; private set; }
        public bool IsPlaylist { get; private set; }
        public int EntryCount { get; private set; }

        public List<FormatChoice> Choices { get; private set; }
        public int DefaultChoice { get; private set; }

        /// <summary>Set when the details could not be read at all.</summary>
        public bool IsFallback { get; private set; }

        private VideoInfo()
        {
            Choices = new List<FormatChoice>();
        }

        /// <summary>"Blender Foundation   ·   14:48"</summary>
        public string MetaLine()
        {
            List<string> parts = new List<string>();
            if (IsPlaylist)
            {
                parts.Add(EntryCount > 0
                    ? "Playlist of " + EntryCount.ToString(CultureInfo.InvariantCulture) + " videos"
                    : "Playlist");
            }
            if (!string.IsNullOrEmpty(Uploader)) { parts.Add(Uploader); }
            if (!string.IsNullOrEmpty(Duration)) { parts.Add(Duration); }
            return string.Join("   ·   ", parts.ToArray());
        }

        // ================= parsing =======================================
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            // A long video's dump lists every fragment of every format and
            // easily runs to several MB.
            json.MaxJsonLength = int.MaxValue;
            json.RecursionLimit = 256;
            return json;
        }

        /// <summary>Null if the text is not a usable dump.</summary>
        public static VideoInfo Parse(string json, AppSettings settings, string playlistItems)
        {
            Dictionary<string, object> j;
            try { j = Json.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
            if (j == null) { return null; }

            VideoInfo info = new VideoInfo();
            info.Title = Str(j, "title");
            info.ThumbnailUrl = Str(j, "thumbnail");

            string uploader = Str(j, "uploader") ?? Str(j, "channel") ?? Str(j, "uploader_id");
            // The engine falls back to its own internal names for sites it
            // has no dedicated support for - not something to show anyone.
            if (uploader != null && uploader.StartsWith("generic", StringComparison.OrdinalIgnoreCase)) { uploader = null; }
            info.Uploader = uploader;

            info.Duration = Str(j, "duration_string") ?? FormatDuration(Num(j, "duration"));

            if (Str(j, "_type") == "playlist")
            {
                info.IsPlaylist = true;
                IList entries = j.ContainsKey("entries") ? j["entries"] as IList : null;
                info.EntryCount = (int)(Num(j, "playlist_count") > 0 ? Num(j, "playlist_count")
                                         : (entries == null ? 0 : entries.Count));
                info.Duration = null;
                info.AddPresetChoices(settings, true, playlistItems);
                return info;
            }

            info.AddFormatChoices(j);
            info.DefaultChoice = PickDefault(info.Choices, settings.DefaultQuality);
            return info;
        }

        /// <summary>
        /// When the details could not be read: the standard choices, so the
        /// download can still be tried - plenty of sites download fine
        /// without describing themselves first.
        /// </summary>
        public static VideoInfo Fallback(AppSettings settings, bool playlist, string playlistItems)
        {
            VideoInfo info = new VideoInfo();
            info.IsFallback = true;
            info.IsPlaylist = playlist;
            info.AddPresetChoices(settings, playlist, playlistItems);
            return info;
        }

        // ================= choices =======================================
        private void AddFormatChoices(Dictionary<string, object> j)
        {
            // JavaScriptSerializer hands back JSON arrays as object[] - IList
            // covers that and ArrayList alike.
            IList formats = j.ContainsKey("formats") ? j["formats"] as IList : null;
            List<Dictionary<string, object>> all = new List<Dictionary<string, object>>();
            if (formats != null)
            {
                foreach (object o in formats)
                {
                    Dictionary<string, object> f = o as Dictionary<string, object>;
                    // Storyboards are preview strips, not something to download.
                    if (f != null && Str(f, "ext") != "mhtml") { all.Add(f); }
                }
            }
            if (all.Count == 0) { all.Add(j); }   // a single-format page describes itself at the top level

            List<Dictionary<string, object>> video = all.Where(
                f => Str(f, "vcodec") != "none" && Num(f, "height") > 0).ToList();
            List<Dictionary<string, object>> audioOnly = all.Where(
                f => Str(f, "vcodec") == "none" && Str(f, "acodec") != null && Str(f, "acodec") != "none").ToList();
            bool hasAudio = all.Any(f => Str(f, "acodec") != "none");

            double bestAudioSize = audioOnly.Count == 0 ? 0 : Size(audioOnly.OrderByDescending(f => Num(f, "tbr")).First());

            if (video.Count == 0)
            {
                // A plain file, or a page that does not say what it holds.
                Dictionary<string, object> best = all.OrderByDescending(f => Num(f, "tbr")).First();
                string ext = (Str(best, "ext") ?? "").ToUpperInvariant();
                Choices.Add(new FormatChoice("Best available", Join(ext, SizeText(Size(best))),
                    FormatChoice.KindVideo, 0, QualityPreset.All[0].Args));
            }
            else
            {
                // One choice per distinct resolution, highest first. Named
                // by the short side, so a vertical 1080x1920 video reads as
                // 1080p like everyone expects, but capped by real height.
                var byLabel = video
                    .GroupBy(f => ShortSide(f))
                    .Where(g => g.Key >= 144)
                    .OrderByDescending(g => g.Key)
                    .Take(7)
                    .ToList();
                if (byLabel.Count == 0) { byLabel = video.GroupBy(f => ShortSide(f)).OrderByDescending(g => g.Key).Take(1).ToList(); }

                bool first = true;
                foreach (var group in byLabel)
                {
                    int height = (int)group.Max(f => Num(f, "height"));
                    double fps = group.Max(f => Num(f, "fps"));

                    // Estimated from the stream most likely to be picked that
                    // lists a size - MP4 first, as the download prefers it.
                    // Streaming-only variants often list none at all.
                    Dictionary<string, object> sized = group
                        .Where(f => Size(f) > 0)
                        .OrderByDescending(f => Str(f, "ext") == "mp4" ? 1 : 0)
                        .ThenByDescending(f => Num(f, "tbr"))
                        .FirstOrDefault();
                    double size = sized == null ? 0 : Size(sized);
                    // A video-only stream gets merged with the best audio.
                    if (size > 0 && Str(sized, "acodec") == "none") { size += bestAudioSize; }

                    string label = ResolutionName(group.Key) + (first ? " (best)" : "");
                    string detail = Join(fps > 31 ? Math.Round(fps).ToString(CultureInfo.InvariantCulture) + " fps" : "",
                                         SizeText(size));
                    Choices.Add(new FormatChoice(label, detail, FormatChoice.KindVideo, height, new string[]
                    {
                        "-f", "bv*[height<=" + height + "]+ba/b[height<=" + height + "]/bv*+ba/b",
                        "-S", QualityPreset.RankBest, "--no-playlist"
                    }));
                    first = false;
                }
            }

            if (hasAudio)
            {
                Dictionary<string, object> bestAudio = audioOnly.OrderByDescending(f => Num(f, "tbr")).FirstOrDefault();
                string ext = bestAudio == null ? "" : (Str(bestAudio, "ext") ?? "").ToUpperInvariant();
                Choices.Add(new FormatChoice("Audio only - MP3", "", FormatChoice.KindAudioMp3, 0, QualityPreset.All[4].Args));
                Choices.Add(new FormatChoice("Audio only - original", Join(ext, SizeText(bestAudioSize)),
                    FormatChoice.KindAudio, 0, QualityPreset.All[5].Args));
            }

            int subs = Count(j, "subtitles");
            if (subs > 0)
            {
                Choices.Add(new FormatChoice("Subtitles only",
                    subs.ToString(CultureInfo.InvariantCulture) + (subs == 1 ? " language" : " languages"),
                    FormatChoice.KindSubs, 0, QualityPreset.All[6].Args));
            }

            Choices.Add(new FormatChoice("More formats...", "pick an exact stream", FormatChoice.KindPicker, 0, null));
        }

        /// <summary>The standard list, for playlists (each video differs) and fallbacks.</summary>
        private void AddPresetChoices(AppSettings settings, bool playlist, string playlistItems)
        {
            int[] caps = { 0, 1080, 720, 480 };
            for (int i = 0; i < 6; i++)
            {
                QualityPreset preset = QualityPreset.All[i];
                string[] args = playlist
                    ? QualityPreset.PlaylistArgs(settings.ArchiveFile, preset.Args, playlistItems)
                    : preset.Args;
                string kind = i == 4 ? FormatChoice.KindAudioMp3 : i == 5 ? FormatChoice.KindAudio : FormatChoice.KindVideo;
                Choices.Add(new FormatChoice(preset.Label, "", kind, i < 4 ? caps[i] : 0, args));
            }
            if (!playlist)
            {
                Choices.Add(new FormatChoice("More formats...", "pick an exact stream", FormatChoice.KindPicker, 0, null));
            }
            // Same order as AppSettings.PreferredQualities.
            DefaultChoice = Math.Min(Math.Max(settings.DefaultQuality, 0), 5);
        }

        /// <summary>Maps the preferred quality onto the closest choice this video has.</summary>
        private static int PickDefault(List<FormatChoice> choices, int preferred)
        {
            int[] caps = { 0, 1080, 720, 480 };
            if (preferred >= 1 && preferred <= 3)
            {
                int lastVideo = -1;
                for (int i = 0; i < choices.Count; i++)
                {
                    if (choices[i].Kind != FormatChoice.KindVideo) { continue; }
                    lastVideo = i;
                    if (choices[i].Height > 0 && choices[i].Height <= caps[preferred]) { return i; }
                }
                return Math.Max(lastVideo, 0);
            }
            if (preferred == 4 || preferred == 5)
            {
                string kind = preferred == 4 ? FormatChoice.KindAudioMp3 : FormatChoice.KindAudio;
                for (int i = 0; i < choices.Count; i++)
                {
                    if (choices[i].Kind == kind) { return i; }
                }
            }
            return 0;
        }

        // ================= helpers =======================================
        private static int ShortSide(Dictionary<string, object> f)
        {
            double h = Num(f, "height");
            double w = Num(f, "width");
            return (int)(w > 0 ? Math.Min(w, h) : h);
        }

        private static string ResolutionName(int shortSide)
        {
            if (shortSide >= 4320) { return "8K"; }
            if (shortSide >= 2160) { return "4K"; }
            return shortSide.ToString(CultureInfo.InvariantCulture) + "p";
        }

        private static double Size(Dictionary<string, object> f)
        {
            double size = Num(f, "filesize");
            return size > 0 ? size : Num(f, "filesize_approx");
        }

        private static string SizeText(double bytes)
        {
            if (bytes <= 0) { return ""; }
            double mb = bytes / 1048576.0;
            if (mb >= 1024) { return "~" + (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB"; }
            if (mb >= 10) { return "~" + mb.ToString("0", CultureInfo.InvariantCulture) + " MB"; }
            return "~" + mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) { return null; }
            TimeSpan t = TimeSpan.FromSeconds(Math.Round(seconds));
            return t.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", t.Minutes, t.Seconds);
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) { return b ?? ""; }
            if (string.IsNullOrEmpty(b)) { return a; }
            return a + "   ·   " + b;
        }

        private static int Count(Dictionary<string, object> m, string key)
        {
            object v;
            if (!m.TryGetValue(key, out v)) { return 0; }
            Dictionary<string, object> d = v as Dictionary<string, object>;
            return d == null ? 0 : d.Count;
        }

        private static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null) { return null; }
            string s = v as string;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static double Num(Dictionary<string, object> m, string key)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null || v is string) { return 0; }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }
}
