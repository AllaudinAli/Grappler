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

        private const string RankBest = "res,fps,ext:mp4:m4a";

        // The Brave extension refers to these by position (see
        // browser-extension/popup.html) - keep the order, or update it there too.
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
        /// Whole playlist or channel. The archive file is what makes
        /// re-running a channel later pick up only the new uploads.
        /// </summary>
        public static string[] PlaylistArgs(string archivePath)
        {
            return new string[]
            {
                "-f", "bv*+ba/b", "-S", RankBest,
                "--yes-playlist",
                "-o", "%(playlist_title,playlist,uploader,extractor)s/%(playlist_index)02d - %(title)s [%(id)s].%(ext)s",
                "--download-archive", archivePath
            };
        }
    }
}
