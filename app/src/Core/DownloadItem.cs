using System;
using System.Threading;
using System.Windows.Media;

namespace Grappler.Core
{
    public enum DownloadState
    {
        Probing,
        Queued,
        Downloading,
        Processing,   // merging / converting / tagging - percent is meaningless here
        Done,
        Failed,
        Cancelled
    }

    /// <summary>
    /// One row in the queue.
    ///
    /// Threading contract, and the reason this class looks the way it does:
    /// yt-dlp emits progress lines far faster than a UI can usefully repaint
    /// - tens per second, per download. Marshalling each one onto the
    /// dispatcher would spend most of the frame budget on notifications
    /// nobody can see.
    ///
    /// So reader threads only ever call Report*, which parks values in
    /// staging fields under a short lock and sets a dirty flag. The UI ticks
    /// a single timer at 10 Hz and calls Flush() on the dispatcher, which is
    /// the only place PropertyChanged is ever raised. Result: at most 10
    /// notifications a second per item, no matter how loud yt-dlp gets.
    /// </summary>
    public sealed class DownloadItem : NotifyBase
    {
        private static int _lastId;
        private readonly object _gate = new object();

        public DownloadItem(string url)
        {
            Id = Interlocked.Increment(ref _lastId);
            Url = url;
            AddedAt = DateTime.UtcNow;

            _title = url;
            _state = DownloadState.Probing;
            _statusText = "Reading video info";

            _stagedTitle = url;
            _stagedState = DownloadState.Probing;
            _stagedStatus = _statusText;
        }

        /// <summary>How the Brave extension refers to this row.</summary>
        public int Id { get; private set; }

        public string Url { get; private set; }

        /// <summary>Web address of the thumbnail, for the Brave extension.</summary>
        public string ThumbnailUrl { get; set; }

        /// <summary>Sort key for "newest / oldest first".</summary>
        public DateTime AddedAt { get; private set; }

        /// <summary>Where the finished file landed. Drives Open / Play.</summary>
        public string FinalPath { get; set; }

        /// <summary>Full yt-dlp output. Only kept for failures, so it can be copied.</summary>
        public string Log { get; set; }

        // ---------- staging (written by reader threads) -----------------
        private bool _dirty;
        private DownloadState _stagedState;
        private double _stagedPercent;
        private string _stagedStatus;
        private string _stagedTitle;
        private string _stagedMeta;
        private string _stagedSize;
        private string _stagedError;
        private string _stagedSpeed;
        private string _stagedEta;
        private ImageSource _stagedThumbnail;

        // ---------- bound properties (UI thread only) -------------------
        private string _title;
        public string Title { get { return _title; } set { Set(ref _title, value); } }

        private string _metaLine;
        public string MetaLine { get { return _metaLine; } set { Set(ref _metaLine, value); } }

        private string _statusText;
        public string StatusText { get { return _statusText; } set { Set(ref _statusText, value); } }

        private string _sizeText;
        public string SizeText { get { return _sizeText; } set { Set(ref _sizeText, value); } }

        private string _errorText;
        public string ErrorText { get { return _errorText; } set { Set(ref _errorText, value); } }

        // Also inside StatusText; kept apart for the Brave extension's layout.
        private string _speedText;
        public string SpeedText { get { return _speedText; } set { Set(ref _speedText, value); } }

        private string _etaText;
        public string EtaText { get { return _etaText; } set { Set(ref _etaText, value); } }

        private double _percent;
        public double Percent { get { return _percent; } set { Set(ref _percent, value); } }

        private ImageSource _thumbnail;
        public ImageSource Thumbnail { get { return _thumbnail; } set { Set(ref _thumbnail, value); } }

        private DownloadState _state;
        public DownloadState State
        {
            get { return _state; }
            set
            {
                if (Set(ref _state, value))
                {
                    Raise("IsIndeterminate");
                    Raise("IsActive");
                    Raise("SortRank");
                }
            }
        }

        /// <summary>Merging and probing have no meaningful percentage.</summary>
        public bool IsIndeterminate
        {
            get { return _state == DownloadState.Processing || _state == DownloadState.Probing; }
        }

        public bool IsActive
        {
            get
            {
                return _state == DownloadState.Probing
                    || _state == DownloadState.Queued
                    || _state == DownloadState.Downloading
                    || _state == DownloadState.Processing;
            }
        }

        /// <summary>Orders "sort by status" the way a person would read it:
        /// what is happening now, then what is waiting, then the results.</summary>
        public int SortRank
        {
            get
            {
                switch (_state)
                {
                    case DownloadState.Downloading: return 0;
                    case DownloadState.Processing:  return 1;
                    case DownloadState.Probing:     return 2;
                    case DownloadState.Queued:      return 3;
                    case DownloadState.Failed:      return 4;
                    case DownloadState.Cancelled:   return 5;
                    default:                        return 6;
                }
            }
        }

        // ---------- called from reader threads ---------------------------
        public void ReportProgress(double percent, string status, string speed, string eta)
        {
            lock (_gate)
            {
                _stagedState = DownloadState.Downloading;
                _stagedPercent = percent;
                _stagedStatus = status;
                _stagedSpeed = speed;
                _stagedEta = eta;
                _dirty = true;
            }
        }

        public void ReportStage(DownloadState state, string status)
        {
            lock (_gate)
            {
                _stagedState = state;
                _stagedStatus = status;
                _dirty = true;
            }
        }

        public void ReportInfo(string title, string meta)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(title)) { _stagedTitle = title; }
                if (meta != null) { _stagedMeta = meta; }
                _dirty = true;
            }
        }

        /// <summary>
        /// The progress template carries the title too, which is the only
        /// title available for sites the probe could not read.
        /// </summary>
        public void ReportInfoTitleOnly(string title)
        {
            if (string.IsNullOrEmpty(title)) { return; }
            lock (_gate)
            {
                if (_stagedTitle == title) { return; }
                _stagedTitle = title;
                _dirty = true;
            }
        }

        public void ReportThumbnail(ImageSource image)
        {
            if (image == null) { return; }
            lock (_gate)
            {
                _stagedThumbnail = image;
                _dirty = true;
            }
        }

        public void ReportDone(string sizeText)
        {
            lock (_gate)
            {
                _stagedState = DownloadState.Done;
                _stagedPercent = 100.0;
                _stagedSize = sizeText;
                _stagedStatus = "Done";
                _stagedError = null;
                _dirty = true;
            }
        }

        public void ReportFailed(string error)
        {
            lock (_gate)
            {
                _stagedState = DownloadState.Failed;
                _stagedError = error;
                _stagedStatus = "Failed";
                _dirty = true;
            }
        }

        /// <summary>Clears the previous attempt's result before a retry.</summary>
        public void ReportProgressReset()
        {
            lock (_gate)
            {
                _stagedPercent = 0;
                _stagedError = null;
                _stagedSize = null;
                _dirty = true;
            }
        }

        /// <summary>
        /// UI thread only. Returns false when nothing changed, so the timer
        /// can skip idle rows instead of touching every item in the queue.
        /// </summary>
        public bool Flush()
        {
            DownloadState state;
            double percent;
            string status, title, meta, size, error, speed, eta;
            ImageSource thumbnail;

            lock (_gate)
            {
                if (!_dirty) { return false; }
                _dirty = false;
                state = _stagedState;
                percent = _stagedPercent;
                status = _stagedStatus;
                title = _stagedTitle;
                meta = _stagedMeta;
                size = _stagedSize;
                error = _stagedError;
                speed = _stagedSpeed;
                eta = _stagedEta;
                thumbnail = _stagedThumbnail;
            }

            State = state;
            Percent = percent;
            StatusText = status;
            Title = title;
            MetaLine = meta;
            SizeText = size;
            ErrorText = error;
            SpeedText = speed;
            EtaText = eta;
            if (thumbnail != null) { Thumbnail = thumbnail; }
            return true;
        }
    }
}
