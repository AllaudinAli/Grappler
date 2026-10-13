using System.Collections.Generic;

namespace Grappler.Core
{
    /// <summary>
    /// The quality choices, layered on top of config\yt-dlp.conf exactly as
    /// the terminal menu does it - same -f and -S strings, so both
    /// front-ends produce identical files.
    ///
    ///   -f  chooses which streams to take
    ///   -S  ranks the candidates: resolution, then framerate, then mp4
    /// </summary>
    public sealed class QualityPreset
    {
        public string Label { get; private set; }
        public string[] Args { get; private set; }

        /// <summary>Opens the format list instead of downloading straight away.</summary>
        public bool PicksFormat { get; private set; }

        private QualityPreset(string label, string[] args, bool picksFormat)
        {
            Label = label;
            Args = args;
            PicksFormat = picksFormat;
        }

        public override string ToString() { return Label; }

        public const string RankBest = "res,fps,ext:mp4:m4a";

        // VideoInfo uses these by position (0-3 video, 4 MP3, 5 original
        // audio, 6 subtitles) - keep the order.
        public static readonly QualityPreset[] All = new QualityPreset[]
        {
            new QualityPreset("Best quality",
                new string[] { "-f", "bv*+ba/b", "-S", RankBest, "--no-playlist" }, false),

            new QualityPreset("1080p or lower",
                new string[] { "-f", "bv*[height<=1080]+ba/b[height<=1080]/bv*+ba/b",
                               "-S", RankBest, "--no-playlist" }, false),

            new QualityPreset("720p or lower",
                new string[] { "-f", "bv*[height<=720]+ba/b[height<=720]/bv*+ba/b",
                               "-S", RankBest, "--no-playlist" }, false),

            new QualityPreset("480p or lower",
                new string[] { "-f", "bv*[height<=480]+ba/b[height<=480]/bv*+ba/b",
                               "-S", RankBest, "--no-playlist" }, false),

            new QualityPreset("Audio only - MP3",
                new string[] { "-f", "ba/b", "-x", "--audio-format", "mp3",
                               "--audio-quality", "0", "--no-playlist" }, false),

            new QualityPreset("Audio only - best",
                new string[] { "-f", "ba/b", "-x", "--audio-format", "best",
                               "--no-playlist" }, false),

            new QualityPreset("Subtitles only",
                new string[] { "--skip-download", "--write-subs", "--write-auto-subs",
                               "--sub-langs", "all", "--convert-subs", "srt",
                               "--no-playlist" }, false),

            new QualityPreset("Choose format...", new string[0], true)
        };

        /// <summary>
        /// Whole playlist or channel, at the quality of the given preset.
        /// The archive file is what makes re-running a channel later pick
        /// up only the new uploads.
        /// </summary>
        public static string[] PlaylistArgs(string archivePath, string[] presetArgs, string items)
        {
            List<string> args = new List<string>();
            foreach (string a in presetArgs)
            {
                if (a != "--no-playlist") { args.Add(a); }
            }
            args.Add("--yes-playlist");
            args.Add("-o");
            args.Add("%(playlist_title,playlist,uploader,extractor)s/%(playlist_index)02d - %(title)s [%(id)s].%(ext)s");
            args.Add("--download-archive");
            args.Add(archivePath);
            if (!string.IsNullOrEmpty(items))
            {
                // Passed through untouched - the engine already understands
                // "1-10", "3,7,12" and "5:", and reports a bad range better
                // than a guess here would.
                args.Add("--playlist-items");
                args.Add(items);
            }
            return args.ToArray();
        }
    }
}
