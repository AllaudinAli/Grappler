using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Grappler.Core;

namespace Grappler
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly DownloadQueue _queue;
        private readonly DispatcherTimer _flushTimer;
        private readonly TrayIcon _tray;

        private bool _quitting;
        private bool _cleanedUp;
        private bool _toldAboutTray;

        private ListCollectionView _view;
        private string _search = "";
        private string _filter = "all";
        private string _lastClipboardOffer = "";
        private bool _normalizingUrlBox;

        // Cached so the 10 Hz tick only touches the visual tree when the
        // text has actually changed.
        private string _lastStatusLine = "";
        private int _lastAll = -1, _lastActive = -1, _lastDone = -1, _lastFailed = -1;

        public ObservableCollection<DownloadItem> Items { get; private set; }

        public MainWindow()
        {
            Items = new ObservableCollection<DownloadItem>();
            _settings = AppSettings.Load();
            _settings.EnsureFolders();
            _queue = new DownloadQueue(_settings);

            _tray = new TrayIcon();
            _tray.OpenRequested += delegate { ShowFromTray(); };
            _tray.FolderRequested += delegate { OpenDownloadFolder(); };
            _tray.QuitRequested += delegate { QuitFromTray(); };

            // Here rather than on Loaded: when the extension starts Grappler
            // the window is never shown, and Loaded never fires.
            BrowserBridge.DeleteAllCookies(_settings);   // leftovers from a crash
            BrowserBridge.Listen(delegate(Dictionary<string, object> message)
            {
                Dictionary<string, object> reply = null;
                Dispatcher.Invoke(new Action(delegate { reply = HandleBrowserMessage(message); }));
                return reply;
            });

            InitializeComponent();
            DataContext = this;

            BuildDropdowns();
            BuildView();

            _flushTimer = new DispatcherTimer(DispatcherPriority.Background);
            _flushTimer.Interval = TimeSpan.FromMilliseconds(100);
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            Loaded += OnLoaded;
            UpdateStatus();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeChrome.Apply(this);
        }

        private void BuildDropdowns()
        {
            QualityBox.ItemsSource = QualityPreset.All;
            QualityBox.SelectedIndex = _settings.DefaultQuality;

            SortBox.ItemsSource = new string[]
            {
                "Newest first", "Oldest first", "Title A-Z", "Status", "Progress"
            };
            SortBox.SelectedIndex = 0;
        }

        /// <summary>
        /// Filtering and sorting run through an ICollectionView rather than
        /// rebuilding the list, so the underlying collection - and the jobs
        /// pointing at it - are never disturbed by a change of view.
        /// </summary>
        private void BuildView()
        {
            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
            _view.Filter = MatchesFilter;

            // Live filtering re-evaluates a row when the named properties
            // change, so a finished download leaves the "Active" tab on its
            // own instead of waiting for the next manual refresh.
            _view.IsLiveFiltering = true;
            _view.LiveFilteringProperties.Add("State");
            _view.LiveFilteringProperties.Add("Title");

            ApplySort();
            Queue.ItemsSource = _view;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!Paths.IsInstalled)
            {
                AddButton.IsEnabled = false;
                UrlBox.IsEnabled = false;
                MessageBox.Show(
                    "yt-dlp.exe is missing from bin\\.\n\nRun Setup.bat in the Grappler folder - it downloads everything Grappler needs.",
                    "Grappler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UrlBox.Focus();

            // Deferred so the window paints first: probing yt-dlp costs a
            // process launch and nothing in the first frame depends on it.
            Task.Factory.StartNew(delegate
            {
                string version = YtDlp.Version();
                string disk = DescribeFreeSpace();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    VersionText.Text = version;
                    DiskText.Text = disk;
                }));
            });
        }

        // ================= adding ======================================
        private void OnAddClick(object sender, RoutedEventArgs e) { AddFromBox(); }

        private void OnUrlBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddFromBox(); e.Handled = true; }
        }

        /// <summary>
        /// WPF pastes newlines into a single-line TextBox verbatim, so a
        /// column of links copied from anywhere renders as several lines
        /// crammed into a 34px box. Flatten them: the parser splits on
        /// spaces just as happily, and the field stays one line.
        /// </summary>
        private void OnUrlBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_normalizingUrlBox) { return; }

            string text = UrlBox.Text;
            if (text == null || (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)) { return; }

            _normalizingUrlBox = true;
            try
            {
                UrlBox.Text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                UrlBox.CaretIndex = UrlBox.Text.Length;
            }
            finally { _normalizingUrlBox = false; }
        }

        private void AddFromBox()
        {
            List<string> urls = ExtractUrls(UrlBox.Text);
            if (urls.Count == 0)
            {
                UrlBox.Focus();
                UrlBox.SelectAll();
                return;
            }
            if (AddUrls(urls)) { UrlBox.Clear(); }
        }

        private bool AddUrls(List<string> urls)
        {
            return AddUrls(urls, SelectedPreset(), PlaylistCheck.IsChecked == true, null);
        }

        private QualityPreset SelectedPreset()
        {
            QualityPreset preset = QualityBox.SelectedItem as QualityPreset;
            return preset ?? QualityPreset.All[0];
        }

        /// <summary>
        /// A link from the Brave extension. Same path as a paste, except the
        /// quality may come from the extension and the playlist box is
        /// ignored - the button means "this page", not "this channel".
        /// Returns null on success or a message for the extension to show.
        /// </summary>
        private string AddFromBrowser(BrowserRequest request)
        {
            if (!Paths.IsInstalled) { return "yt-dlp.exe is missing from Grappler's bin folder."; }

            QualityPreset preset = request.Quality >= 0 && request.Quality < QualityPreset.All.Length
                ? QualityPreset.All[request.Quality]
                : SelectedPreset();

            // The format list is a dialog, so it needs to be seen.
            if (preset.PicksFormat)
            {
                if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; }
                Activate();
            }

            string cookieFile = BrowserBridge.SaveCookies(_settings, request.Cookies);
            List<string> urls = new List<string>();
            urls.Add(request.Url);

            if (!AddUrls(urls, preset, false, cookieFile))
            {
                BrowserBridge.DeleteCookies(cookieFile);
                return "Cancelled in Grappler.";
            }
            return null;
        }

        private bool AddUrls(List<string> urls, QualityPreset preset, bool playlist, string cookieFile)
        {
            string[] args;

            if (preset.PicksFormat)
            {
                // The format list is per-video, so this one cannot be
                // applied to a batch the way the presets can.
                if (urls.Count != 1)
                {
                    MessageBox.Show(
                        "Choosing a format works on one link at a time - the available formats differ per video.",
                        "Grappler", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                FormatWindow picker = new FormatWindow(urls[0], _settings);
                picker.Owner = this;
                if (picker.ShowDialog() != true) { return false; }

                args = new string[] { "-f", picker.ChosenFormat, "--no-playlist" };
            }
            else if (playlist)
            {
                List<string> playlistArgs =
                    new List<string>(QualityPreset.PlaylistArgs(_settings.ArchiveFile));

                // Passed through to yt-dlp untouched - it already
                // understands "1-10", "3,7,12" and "5:" and reports a bad
                // range better than a guess here would.
                string range = PlaylistItemsBox.Text == null
                    ? "" : PlaylistItemsBox.Text.Trim();
                if (range.Length > 0)
                {
                    playlistArgs.Add("--playlist-items");
                    playlistArgs.Add(range);
                }
                args = playlistArgs.ToArray();
            }
            else
            {
                args = preset.Args;
            }

            foreach (string url in urls)
            {
                DownloadItem item = new DownloadItem(url);
                Items.Add(item);
                _queue.Enqueue(item, args, cookieFile);
            }

            UpdateStatus();
            return true;
        }

        /// <summary>
        /// Accepts one link, a paste of many, or the contents of a text
        /// file - all the same way. Duplicates within a single batch are
        /// dropped, matching what the terminal menu does.
        /// </summary>
        private static List<string> ExtractUrls(string text)
        {
            List<string> found = new List<string>();
            if (string.IsNullOrEmpty(text)) { return found; }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] tokens = text.Split(
                new char[] { '\r', '\n', ' ', '\t', '"', '\'', '<', '>' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string token in tokens)
            {
                string url = token.Trim().TrimEnd(',', ';');
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (seen.Add(url)) { found.Add(url); }
            }
            return found;
        }

        // ================= per-row actions ==============================
        private static DownloadItem ItemOf(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as DownloadItem;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DownloadItem item = ItemOf(sender);
            if (item != null) { _queue.Cancel(item); }
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            DownloadItem item = ItemOf(sender);
            if (item != null) { _queue.Retry(item); }
        }

        private void OnCopyLogClick(object sender, RoutedEventArgs e)
        {
            DownloadItem item = ItemOf(sender);
            if (item == null) { return; }

            string text = !string.IsNullOrEmpty(item.Log) ? item.Log : item.ErrorText;
            if (string.IsNullOrEmpty(text)) { return; }
            try { Clipboard.SetText(item.Url + Environment.NewLine + Environment.NewLine + text); }
            catch { }
        }

        private void OnShowInFolderClick(object sender, RoutedEventArgs e)
        {
            DownloadItem item = ItemOf(sender);
            if (item != null) { ShowInFolder(item); }
        }

        private void ShowInFolder(DownloadItem item)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.FinalPath) && File.Exists(item.FinalPath))
                {
                    Process.Start("explorer.exe", "/select,\"" + item.FinalPath + "\"");
                }
                else
                {
                    Process.Start("explorer.exe", "\"" + _settings.DownloadFolder + "\"");
                }
            }
            catch { }
        }

        private void OnPlayClick(object sender, RoutedEventArgs e)
        {
            DownloadItem item = ItemOf(sender);
            if (item != null) { Play(item); }
        }

        private void Play(DownloadItem item)
        {
            if (string.IsNullOrEmpty(item.FinalPath)) { return; }
            try
            {
                if (File.Exists(item.FinalPath)) { Process.Start("\"" + item.FinalPath + "\""); }
            }
            catch { }
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsWindow dialog = new SettingsWindow(_settings);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true)
            {
                // A raised parallel limit should take effect immediately,
                // not at the next Add.
                _queue.Refresh();
                DiskText.Text = DescribeFreeSpace();
            }
        }

        // ================= filter and sort ==============================
        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            if (_view == null) { return; }
            if (sender == SegActive)      { _filter = "active"; }
            else if (sender == SegDone)   { _filter = "done"; }
            else if (sender == SegFailed) { _filter = "failed"; }
            else                          { _filter = "all"; }
            _view.Refresh();
            UpdateStatus();
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_view == null) { return; }
            _search = SearchBox.Text == null ? "" : SearchBox.Text.Trim();
            _view.Refresh();
            UpdateStatus();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                SearchBox.Clear();
                e.Handled = true;
            }
        }

        private void OnSortChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_view == null) { return; }
            ApplySort();
        }

        /// <summary>
        /// Drops finished, failed and cancelled rows. Anything still running
        /// is left alone - this is a tidy-up, not a stop button.
        /// </summary>
        private void OnClearFinishedClick(object sender, RoutedEventArgs e)
        {
            ClearFinished();
        }

        private void ClearFinished()
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                DownloadItem item = Items[i];
                if (item.IsActive) { continue; }

                _queue.Forget(item);
                Items.RemoveAt(i);
            }
            UpdateStatus();
        }

        private void ApplySort()
        {
            using (_view.DeferRefresh())
            {
                _view.SortDescriptions.Clear();
                _view.LiveSortingProperties.Clear();

                switch (SortBox.SelectedIndex)
                {
                    case 1:
                        _view.SortDescriptions.Add(
                            new SortDescription("AddedAt", ListSortDirection.Ascending));
                        break;
                    case 2:
                        _view.SortDescriptions.Add(
                            new SortDescription("Title", ListSortDirection.Ascending));
                        _view.LiveSortingProperties.Add("Title");
                        break;
                    case 3:
                        _view.SortDescriptions.Add(
                            new SortDescription("SortRank", ListSortDirection.Ascending));
                        _view.SortDescriptions.Add(
                            new SortDescription("AddedAt", ListSortDirection.Descending));
                        _view.LiveSortingProperties.Add("SortRank");
                        break;
                    case 4:
                        _view.SortDescriptions.Add(
                            new SortDescription("Percent", ListSortDirection.Descending));
                        _view.LiveSortingProperties.Add("Percent");
                        break;
                    default:
                        _view.SortDescriptions.Add(
                            new SortDescription("AddedAt", ListSortDirection.Descending));
                        break;
                }

                // Only on for the keys that actually move. Live-sorting by
                // date added would re-sort the whole view on every progress
                // tick for no benefit.
                _view.IsLiveSorting = _view.LiveSortingProperties.Count > 0;
            }
        }

        private bool MatchesFilter(object candidate)
        {
            DownloadItem item = candidate as DownloadItem;
            if (item == null) { return false; }

            switch (_filter)
            {
                case "active":
                    if (!item.IsActive) { return false; }
                    break;
                case "done":
                    if (item.State != DownloadState.Done) { return false; }
                    break;
                case "failed":
                    if (item.State != DownloadState.Failed &&
                        item.State != DownloadState.Cancelled) { return false; }
                    break;
            }

            if (_search.Length > 0)
            {
                bool inTitle = item.Title != null &&
                    item.Title.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
                bool inUrl = item.Url.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!inTitle && !inUrl) { return false; }
            }
            return true;
        }

        // ================= the 10 Hz tick ===============================
        private void OnFlushTick(object sender, EventArgs e)
        {
            bool anyChanged = false;
            for (int i = 0; i < Items.Count; i++)
            {
                DownloadItem item = Items[i];
                bool wasActive = item.IsActive;
                if (!item.Flush()) { continue; }
                anyChanged = true;
                if (wasActive && !item.IsActive) { NotifyFinished(item); }
            }
            if (anyChanged) { UpdateStatus(); }
        }

        private void UpdateStatus()
        {
            int running = 0, queued = 0, done = 0, failed = 0, active = 0;

            for (int i = 0; i < Items.Count; i++)
            {
                switch (Items[i].State)
                {
                    case DownloadState.Downloading:
                    case DownloadState.Processing:
                    case DownloadState.Probing:
                        running++; active++;
                        break;
                    case DownloadState.Queued:
                        queued++; active++;
                        break;
                    case DownloadState.Done:
                        done++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            SetCount(SegAll, Items.Count, ref _lastAll);
            SetCount(SegActive, active, ref _lastActive);
            SetCount(SegDone, done, ref _lastDone);
            SetCount(SegFailed, failed, ref _lastFailed);

            string line = string.Format(
                "{0} running   ·   {1} queued   ·   {2} done   ·   {3} failed",
                running, queued, done, failed);
            if (line != _lastStatusLine)
            {
                QueueStatus.Text = line;
                _lastStatusLine = line;
                _tray.SetTooltip(active == 0
                    ? "Grappler"
                    : string.Format("Grappler - {0} downloading, {1} queued", running, queued));
            }

            if (Items.Count == 0)
            {
                EmptyTitle.Text = "Nothing in the queue";
                EmptyHint.Text = "Paste a link above, or drop one onto this window";
            }
            else
            {
                EmptyTitle.Text = "No matches";
                EmptyHint.Text = "Nothing here matches the current filter";
            }
        }

        private static void SetCount(RadioButton segment, int value, ref int cached)
        {
            if (value == cached) { return; }
            cached = value;
            segment.Tag = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ================= window plumbing ==============================
        private void OnWindowActivated(object sender, EventArgs e)
        {
            // Offer whatever is on the clipboard, but only once per copy and
            // never over something already typed.
            if (UrlBox == null || UrlBox.Text.Length > 0) { return; }
            try
            {
                if (!Clipboard.ContainsText()) { return; }
                string text = Clipboard.GetText().Trim();
                if (text == _lastClipboardOffer) { return; }

                List<string> urls = ExtractUrls(text);
                if (urls.Count == 0) { return; }

                // Offer the links themselves, not the surrounding prose the
                // clipboard may have come wrapped in.
                _lastClipboardOffer = text;
                UrlBox.Text = string.Join(" ", urls.ToArray());
                UrlBox.CaretIndex = UrlBox.Text.Length;
            }
            catch { }
        }

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            bool usable = e.Data.GetDataPresent(DataFormats.FileDrop) ||
                          e.Data.GetDataPresent(DataFormats.Text);
            e.Effects = usable ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnWindowDrop(object sender, DragEventArgs e)
        {
            List<string> urls = new List<string>();

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    foreach (string file in files)
                    {
                        try { urls.AddRange(ExtractUrls(File.ReadAllText(file))); }
                        catch { }
                    }
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                urls.AddRange(ExtractUrls(e.Data.GetData(DataFormats.Text) as string));
            }

            if (urls.Count > 0) { AddUrls(urls); }
            e.Handled = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_quitting) { return; }

            string action = _settings.CloseAction;
            if (action == AppSettings.CloseAsk)
            {
                CloseWindow dialog = new CloseWindow(CountActive());
                dialog.Owner = this;
                if (dialog.ShowDialog() != true)
                {
                    e.Cancel = true;
                    return;
                }
                action = dialog.Choice;
                if (dialog.Remember)
                {
                    _settings.CloseAction = action;
                    _settings.Save();
                }
            }

            if (action == AppSettings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _quitting = true;
            Cleanup();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Cleanup();
            Application.Current.Shutdown();
        }

        /// <summary>
        /// Everything that must happen before the process goes, whichever
        /// way it goes. Otherwise yt-dlp and ffmpeg keep running headless,
        /// holding part-files open, and sign-in cookies stay on disk.
        /// </summary>
        private void Cleanup()
        {
            if (_cleanedUp) { return; }
            _cleanedUp = true;

            _flushTimer.Stop();
            _queue.CancelAll();
            BrowserBridge.DeleteAllCookies(_settings);
            _settings.DefaultQuality = QualityBox.SelectedIndex < 0 ? 0 : QualityBox.SelectedIndex;
            _settings.Save();
            _tray.Dispose();
        }

        // ================= tray =========================================
        private void ShowFromTray()
        {
            if (!IsVisible) { Show(); }
            if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; }

            // Windows only lets a window take the foreground in limited
            // circumstances; flicking Topmost is the dependable nudge.
            Topmost = true;
            Topmost = false;
            Activate();
        }

        private void HideToTray()
        {
            Hide();
            if (_toldAboutTray) { return; }
            _toldAboutTray = true;
            _tray.Notify("Grappler is still running",
                "Downloads carry on in the background. Click the tray icon to open the window.",
                false, ShowFromTray);
        }

        private void QuitFromTray()
        {
            int active = CountActive();
            if (active > 0)
            {
                MessageBoxResult answer = MessageBox.Show(
                    (active == 1 ? "1 download is" : active + " downloads are") +
                    " still running. Quit and stop " + (active == 1 ? "it" : "them") + "?",
                    "Grappler", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) { return; }
            }
            _quitting = true;
            Cleanup();
            Application.Current.Shutdown();
        }

        /// <summary>A Windows notification - only while the window is out of sight.</summary>
        private void NotifyFinished(DownloadItem item)
        {
            if (_cleanedUp || (IsVisible && WindowState != WindowState.Minimized)) { return; }

            if (item.State == DownloadState.Done)
            {
                _tray.Notify("Download finished", item.Title, false,
                    delegate { ShowInFolder(item); });
            }
            else if (item.State == DownloadState.Failed)
            {
                _tray.Notify("Download failed", item.Title, true, ShowFromTray);
            }
        }

        private int CountActive()
        {
            int active = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].IsActive) { active++; }
            }
            return active;
        }

        private void OpenDownloadFolder()
        {
            try { Process.Start("explorer.exe", "\"" + _settings.DownloadFolder + "\""); }
            catch { }
        }

        // ================= Brave extension ==============================
        /// <summary>UI thread. Every request from the extension lands here.</summary>
        private Dictionary<string, object> HandleBrowserMessage(Dictionary<string, object> message)
        {
            switch (BrowserBridge.Str(message, "type"))
            {
                case "grab":
                    return BrowserBridge.Result(AddFromBrowser(BrowserRequest.FromMessage(message)));
                case "show":
                    ShowFromTray();
                    return BrowserBridge.Result(null);
                case "status":
                    return BuildStatus();
                case "action":
                    return BrowserBridge.Result(DoBrowserAction(
                        BrowserBridge.Str(message, "action"), BrowserBridge.Int(message, "id", -1)));
                default:
                    return BrowserBridge.Result("Unknown request.");
            }
        }

        /// <summary>The queue as the extension's popup shows it, newest first.</summary>
        private Dictionary<string, object> BuildStatus()
        {
            List<object> rows = new List<object>();
            for (int i = Items.Count - 1; i >= 0 && rows.Count < 30; i--)
            {
                DownloadItem item = Items[i];
                Dictionary<string, object> row = new Dictionary<string, object>();
                row["id"] = item.Id;
                row["url"] = item.Url;
                row["title"] = item.Title;
                row["state"] = item.State.ToString().ToLowerInvariant();
                row["percent"] = item.Percent;
                row["status"] = item.StatusText;
                row["speed"] = item.SpeedText;
                row["eta"] = item.EtaText;
                row["meta"] = item.MetaLine;
                row["size"] = item.SizeText;
                row["error"] = item.ErrorText;
                row["thumb"] = item.ThumbnailUrl;
                rows.Add(row);
            }

            Dictionary<string, object> reply = BrowserBridge.Result(null);
            reply["running"] = true;
            reply["items"] = rows;
            return reply;
        }

        private string DoBrowserAction(string action, int id)
        {
            if (action == "clear") { ClearFinished(); return null; }
            if (action == "folder" && id < 0) { OpenDownloadFolder(); return null; }

            DownloadItem item = null;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Id == id) { item = Items[i]; break; }
            }
            if (item == null) { return "That download is no longer in Grappler."; }

            switch (action)
            {
                case "cancel": _queue.Cancel(item); break;
                case "retry":  _queue.Retry(item); break;
                case "folder": ShowInFolder(item); break;
                case "play":   Play(item); break;
                case "remove":
                    if (item.IsActive) { return "Cancel it first."; }
                    _queue.Forget(item);
                    Items.Remove(item);
                    UpdateStatus();
                    break;
                default: return "Unknown action.";
            }
            return null;
        }

        private string DescribeFreeSpace()
        {
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_settings.DownloadFolder));
                return string.Format("{0:N1} GB free", drive.AvailableFreeSpace / 1073741824.0);
            }
            catch { return ""; }
        }
    }
}
