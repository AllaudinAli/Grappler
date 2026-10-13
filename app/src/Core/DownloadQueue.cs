using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Grappler.Core
{
    /// <summary>
    /// Runs downloads, at most MaxParallel at a time.
    ///
    /// The queue never reads DownloadItem.State to decide what to run next.
    /// That property is deliberately stale - it is only refreshed 10 times a
    /// second by the UI's flush timer - so scheduling off it would race and
    /// occasionally start the same job twice. Each job carries its own
    /// authoritative status behind the lock instead, and the item is written
    /// to purely for display.
    /// </summary>
    public sealed class DownloadQueue
    {
        private enum JobStatus { Probing, Ready, Running, Finished }

        private sealed class Job
        {
            public DownloadItem Item;
            public string[] ExtraArgs;
            public string CookieFile;   // from the Brave extension, or null
            public JobStatus Status;
            public Process Process;
            public volatile bool Cancelled;
            public string FinalPath;
            public string Error;
        }

        /// <summary>
        /// Probing is a yt-dlp process too, so it needs a limit of its own.
        /// Without one, pasting fifty links would launch fifty processes at
        /// once - enough to bog the machine down and to get the site to
        /// start refusing requests before a single download had begun.
        /// </summary>
        private const int MaxProbes = 3;

        private readonly List<Job> _jobs = new List<Job>();
        private readonly Queue<Job> _toProbe = new Queue<Job>();
        private readonly object _gate = new object();
        private readonly AppSettings _settings;
        private int _running;
        private int _probing;

        public DownloadQueue(AppSettings settings)
        {
            _settings = settings;
        }

        // ---------- public API ------------------------------------------
        public void Enqueue(DownloadItem item, string[] extraArgs)
        {
            Enqueue(item, extraArgs, null);
        }

        public void Enqueue(DownloadItem item, string[] extraArgs, string cookieFile)
        {
            Job job = new Job();
            job.Item = item;
            job.ExtraArgs = extraArgs;
            job.CookieFile = cookieFile;
            job.Status = JobStatus.Probing;

            lock (_gate)
            {
                _jobs.Add(job);
                _toProbe.Enqueue(job);
            }

            // The row is already on screen saying "Reading video info", so
            // waiting for a probe slot costs the user nothing.
            PumpProbes();
        }

        private void PumpProbes()
        {
            while (true)
            {
                Job next = null;
                lock (_gate)
                {
                    if (_probing >= MaxProbes || _toProbe.Count == 0) { return; }
                    next = _toProbe.Dequeue();
                    _probing++;
                }

                Job started = next;
                Task.Factory.StartNew(delegate
                {
                    try { ProbeThenQueue(started); }
                    finally
                    {
                        lock (_gate) { _probing--; }
                        PumpProbes();
                    }
                });
            }
        }

        public void Cancel(DownloadItem item)
        {
            Job job = Find(item);
            if (job == null) { return; }

            lock (_gate)
            {
                if (job.Status == JobStatus.Finished) { return; }
                job.Cancelled = true;

                if (job.Status != JobStatus.Running)
                {
                    job.Status = JobStatus.Finished;
                    item.ReportStage(DownloadState.Cancelled, "Cancelled");
                    return;
                }
            }

            // Outside the lock: killing a process tree shells out to
            // taskkill and can take a moment.
            ProcessTree.Kill(job.Process);
        }

        public void Retry(DownloadItem item)
        {
            Job job = Find(item);
            if (job == null) { return; }

            lock (_gate)
            {
                if (job.Status == JobStatus.Running) { return; }
                job.Cancelled = false;
                job.Error = null;
                job.FinalPath = null;
                job.Status = JobStatus.Ready;
            }

            item.ReportProgressReset();
            item.ReportStage(DownloadState.Queued, "Queued");
            Pump();
        }

        public void CancelAll()
        {
            List<Job> snapshot;
            lock (_gate) { snapshot = new List<Job>(_jobs); }
            foreach (Job job in snapshot)
            {
                Cancel(job.Item);
            }
        }

        public void Forget(DownloadItem item)
        {
            Job job = Find(item);
            if (job == null) { return; }
            Cancel(item);
            lock (_gate) { _jobs.Remove(job); }
            BrowserBridge.DeleteCookies(job.CookieFile);
        }

        /// <summary>Called when Settings changes the parallel limit.</summary>
        public void Refresh() { Pump(); }

        private Job Find(DownloadItem item)
        {
            lock (_gate)
            {
                for (int i = 0; i < _jobs.Count; i++)
                {
                    if (ReferenceEquals(_jobs[i].Item, item)) { return _jobs[i]; }
                }
            }
            return null;
        }

        // ---------- probe ------------------------------------------------
        private void ProbeThenQueue(Job job)
        {
            DownloadItem item = job.Item;

            // Cancelled while it sat in the probe queue - do not spend a
            // process on it.
            lock (_gate)
            {
                if (job.Cancelled || job.Status != JobStatus.Probing) { return; }
            }

            ProbeResult probe = null;
            try { probe = YtDlp.Probe(item.Url, _settings, job.CookieFile); }
            catch { }

            if (job.Cancelled) { return; }

            if (probe != null)
            {
                item.ReportInfo(probe.Title, probe.MetaLine());
                item.ThumbnailUrl = probe.ThumbnailUrl;

                // Fetched now rather than on completion: seeing the video you
                // asked for appear straight away is most of the reassurance
                // that the right link was pasted.
                if (!string.IsNullOrEmpty(probe.ThumbnailUrl))
                {
                    Task.Factory.StartNew(delegate
                    {
                        ImageSource image = Thumbnails.Get(probe.ThumbnailUrl);
                        if (image != null) { item.ReportThumbnail(image); }
                    });
                }
            }
            else
            {
                // Probing fails on plenty of sites that download perfectly
                // well, so this is not fatal - just carry on with the URL as
                // the title.
                item.ReportInfo(item.Url, "Could not read details - trying anyway");
            }

            lock (_gate)
            {
                if (job.Status != JobStatus.Probing) { return; }
                job.Status = JobStatus.Ready;
            }
            item.ReportStage(DownloadState.Queued, "Queued");
            Pump();
        }

        // ---------- scheduling --------------------------------------------
        private void Pump()
        {
            while (true)
            {
                Job next = null;
                lock (_gate)
                {
                    if (_running >= _settings.MaxParallel) { return; }
                    for (int i = 0; i < _jobs.Count; i++)
                    {
                        if (_jobs[i].Status == JobStatus.Ready)
                        {
                            next = _jobs[i];
                            break;
                        }
                    }
                    if (next == null) { return; }
                    next.Status = JobStatus.Running;
                    _running++;
                }

                Job started = next;
                Task.Factory.StartNew(
                    delegate { Execute(started); },
                    TaskCreationOptions.LongRunning);
            }
        }

        private void Execute(Job job)
        {
            DownloadItem item = job.Item;
            StringBuilder log = new StringBuilder();
            int exit = -1;

            try
            {
                List<string> args = YtDlp.DownloadBase(_settings, job.CookieFile);
                args.Add("--newline");                 // one progress line at a time
                args.Add("--color"); args.Add("never");
                args.Add("--progress-template"); args.Add(YtDlp.ProgressTemplate);

                // A single download gets the full 4 fragments; several
                // sharing one connection get 2 each, matching what the
                // terminal menu does.
                args.Add("--concurrent-fragments");
                args.Add(_settings.MaxParallel > 1 ? "2" : "4");

                args.AddRange(job.ExtraArgs);
                args.Add(item.Url);

                item.ReportStage(DownloadState.Downloading, "Starting");

                using (Process process = new Process())
                {
                    process.StartInfo = YtDlp.StartInfo(args);
                    process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) { return; }
                        lock (log) { log.AppendLine(e.Data); }
                        ParseLine(job, e.Data);
                    };
                    process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) { return; }
                        lock (log) { log.AppendLine(e.Data); }
                        ParseLine(job, e.Data);
                    };

                    process.Start();
                    job.Process = process;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    exit = process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                job.Error = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    job.Status = JobStatus.Finished;
                    _running--;
                }
            }

            if (job.Cancelled)
            {
                item.ReportStage(DownloadState.Cancelled, "Cancelled");
            }
            else if (exit == 0)
            {
                item.FinalPath = job.FinalPath;
                item.ReportDone(DescribeSize(job.FinalPath));
            }
            else
            {
                item.Log = log.ToString();
                item.ReportFailed(job.Error != null
                    ? job.Error
                    : "yt-dlp stopped with exit code " + exit.ToString(CultureInfo.InvariantCulture));
            }

            Pump();
        }

        // ---------- output parsing -----------------------------------------
        private static readonly Regex MovedRx = new Regex(
            "^\\[MoveFiles\\] Moving file .+ to \"(.+)\"\\s*$", RegexOptions.Compiled);
        private static readonly Regex DestinationRx = new Regex(
            "^\\[download\\] Destination: (.+)$", RegexOptions.Compiled);
        private static readonly Regex AlreadyRx = new Regex(
            "^\\[download\\] (.+) has already been downloaded", RegexOptions.Compiled);

        private static void ParseLine(Job job, string line)
        {
            if (line.Length == 0) { return; }

            if (line.StartsWith("PROG@@", StringComparison.Ordinal))
            {
                ParseProgress(job, line);
                return;
            }

            if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                // The card already shows a red alert icon, so the shouty
                // prefix adds nothing - and neither does the extractor tag,
                // since the link it came from is on the same row.
                job.Error = StripExtractorTag(line.Substring(6).Trim());
                return;
            }

            if (line.StartsWith("[Merger]", StringComparison.Ordinal))
            {
                job.Item.ReportStage(DownloadState.Processing, "Merging audio and video");
                return;
            }
            if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
            {
                job.Item.ReportStage(DownloadState.Processing, "Converting audio");
                return;
            }
            if (line.StartsWith("[Metadata]", StringComparison.Ordinal) ||
                line.StartsWith("[EmbedThumbnail]", StringComparison.Ordinal) ||
                line.StartsWith("[ThumbnailsConvertor]", StringComparison.Ordinal) ||
                line.StartsWith("[EmbedSubtitle]", StringComparison.Ordinal))
            {
                job.Item.ReportStage(DownloadState.Processing, "Tagging");
                return;
            }

            Match moved = MovedRx.Match(line);
            if (moved.Success)
            {
                job.FinalPath = moved.Groups[1].Value;
                job.Item.ReportStage(DownloadState.Processing, "Saving");
                return;
            }

            // Fallbacks for runs with no post-processing step, where nothing
            // is ever "moved" and the destination line is all there is.
            Match already = AlreadyRx.Match(line);
            if (already.Success) { job.FinalPath = already.Groups[1].Value.Trim(); return; }

            Match dest = DestinationRx.Match(line);
            if (dest.Success && string.IsNullOrEmpty(job.FinalPath))
            {
                job.FinalPath = dest.Groups[1].Value.Trim();
            }
        }

        /// <summary>"[youtube] abc123: Video unavailable" -> "abc123: Video
        /// unavailable". Only the leading tag goes; the rest is yt-dlp's
        /// own wording and is left exactly as written.</summary>
        private static string StripExtractorTag(string message)
        {
            if (message.Length == 0 || message[0] != '[') { return message; }
            int close = message.IndexOf("] ", StringComparison.Ordinal);
            if (close < 0) { return message; }
            return message.Substring(close + 2).Trim();
        }

        private static void ParseProgress(Job job, string line)
        {
            string[] f = line.Split(new string[] { "@@" }, 7, StringSplitOptions.None);
            if (f.Length < 7) { return; }

            double percent;
            if (!double.TryParse(Digits(f[1]), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out percent))
            {
                percent = 0;
            }

            string title = f[6].Trim();
            if (title.Length > 0) { job.Item.ReportInfoTitleOnly(title); }

            string speed = SpaceUnit(Tidy(f[2]));
            string eta = TidyEta(f[3]);
            job.Item.ReportProgress(percent, FormatStatus(percent, f[2], f[3]), speed, eta);
        }

        private static string Digits(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsDigit(c) || c == '.') { sb.Append(c); }
            }
            return sb.ToString();
        }

        private static string FormatStatus(double percent, string speed, string eta)
        {
            List<string> parts = new List<string>(3);
            parts.Add(percent.ToString("0", CultureInfo.InvariantCulture) + "%");

            string cleanSpeed = Tidy(speed);
            if (cleanSpeed.Length > 0) { parts.Add(SpaceUnit(cleanSpeed)); }

            string cleanEta = TidyEta(eta);
            if (cleanEta.Length > 0) { parts.Add(cleanEta + " left"); }

            return string.Join("   ·   ", parts.ToArray());
        }

        /// <summary>
        /// yt-dlp pads every field, so a five-second ETA arrives as "00:05".
        /// Drop whole leading zero groups ("00:01:23" -> "1:23") and the
        /// leading zero of the first group - but never leave it empty, or
        /// "00:05" would render as ":05".
        /// </summary>
        private static string TidyEta(string eta)
        {
            string clean = Tidy(eta);
            if (clean.Length == 0) { return ""; }

            string[] parts = clean.Split(':');
            if (parts.Length < 2) { return clean; }

            int first = 0;
            while (parts.Length - first > 2 && IsAllZero(parts[first])) { first++; }

            string head = parts[first].TrimStart('0');
            if (head.Length == 0) { head = "0"; }

            StringBuilder sb = new StringBuilder(head);
            for (int i = first + 1; i < parts.Length; i++)
            {
                sb.Append(':').Append(parts[i]);
            }
            return sb.ToString();
        }

        private static bool IsAllZero(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '0') { return false; }
            }
            return text.Length > 0;
        }

        private static string Tidy(string value)
        {
            if (value == null) { return ""; }
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed == "Unknown" ||
                trimmed == "N/A" || trimmed == "NA" || trimmed.IndexOf("--") >= 0)
            {
                return "";
            }
            return trimmed;
        }

        /// <summary>"3.10MiB/s" reads better as "3.10 MiB/s".</summary>
        private static string SpaceUnit(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]) && value[i] != '.')
                {
                    return value.Substring(0, i) + " " + value.Substring(i);
                }
            }
            return value;
        }

        private static string DescribeSize(string path)
        {
            if (string.IsNullOrEmpty(path)) { return ""; }
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists) { return ""; }

                double mb = file.Length / 1048576.0;
                if (mb >= 1024.0)
                {
                    return (mb / 1024.0).ToString("N2", CultureInfo.InvariantCulture) + " GB";
                }
                return mb.ToString("N1", CultureInfo.InvariantCulture) + " MB";
            }
            catch { return ""; }
        }
    }
}
