using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Grappler.Core;

namespace Grappler
{
    public partial class SettingsWindow : Window
    {
        private static readonly string[] Browsers = new string[]
        {
            "None", "chrome", "firefox", "edge", "brave", "chromium", "opera", "vivaldi"
        };

        private static readonly string[] CloseLabels = new string[]
        {
            "Ask every time", "Keep running in the tray", "Quit Grappler"
        };
        private static readonly string[] CloseValues = new string[]
        {
            AppSettings.CloseAsk, AppSettings.CloseToTray, AppSettings.CloseQuits
        };

        private readonly AppSettings _settings;

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();

            FolderBox.Text = settings.DownloadFolder;

            string[] counts = new string[8];
            for (int i = 0; i < 8; i++)
            {
                counts[i] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            ParallelBox.ItemsSource = counts;
            ParallelBox.SelectedIndex = settings.MaxParallel - 1;

            CookieBox.ItemsSource = Browsers;
            CookieBox.SelectedIndex = 0;
            for (int i = 1; i < Browsers.Length; i++)
            {
                if (string.Equals(Browsers[i], settings.CookieBrowser,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    CookieBox.SelectedIndex = i;
                    break;
                }
            }

            QualityBox.ItemsSource = AppSettings.PreferredQualities;
            QualityBox.SelectedIndex = settings.DefaultQuality;

            ShowBraveState();

            CloseBox.ItemsSource = CloseLabels;
            CloseBox.SelectedIndex = Math.Max(0, Array.IndexOf(CloseValues, settings.CloseAction));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeChrome.Apply(this);
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog =
                       new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Where should downloads be saved?";
                dialog.ShowNewFolderButton = true;
                try
                {
                    if (Directory.Exists(FolderBox.Text)) { dialog.SelectedPath = FolderBox.Text; }
                }
                catch { }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    FolderBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            UpdateButton.IsEnabled = false;
            UpdateStatus.Text = "Checking...";

            Task.Factory.StartNew(delegate
            {
                int exit;
                string[] lines = YtDlp.Capture(new string[] { "-U" }, 120000, out exit);
                string output = string.Join("\n", lines);

                // The engine's own wording names the engine; say it plainly.
                string message;
                if (exit != 0)
                {
                    message = "Couldn't update - check your connection and try again.";
                }
                else if (output.IndexOf("up to date", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message = "Already up to date (" + YtDlp.Version().Replace("Engine ", "version ") + ")";
                }
                else
                {
                    message = "Updated to " + YtDlp.Version().Replace("Engine ", "version ");
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    UpdateStatus.Text = message;
                    UpdateButton.IsEnabled = true;
                }));
            });
        }

        // ---------- Brave extension ---------------------------------------
        private void ShowBraveState()
        {
            bool connected = BraveExtension.IsConnected;
            BraveButton.Content = connected ? "Disconnect" : "Connect";
            BraveStatus.Text = connected ? "Connected" : "Not connected";
        }

        private void OnBraveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (BraveExtension.IsConnected)
                {
                    BraveExtension.Disconnect();
                    BraveHint.Text = "Disconnected. You can also remove the extension in brave://extensions.";
                }
                else
                {
                    BraveExtension.Connect();
                    try { Clipboard.SetText(Paths.ExtensionDir); } catch { }
                    BraveHint.Text =
                        "Connected. If the extension isn't in Brave yet: open brave://extensions, turn on Developer mode, " +
                        "click Load unpacked and choose this folder (its path is on your clipboard):\n" + Paths.ExtensionDir;
                }
            }
            catch (Exception ex)
            {
                BraveHint.Text = "Couldn't change the connection: " + ex.Message;
            }
            ShowBraveState();
        }

        private void OnOpenConfigClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("explorer.exe",
                    "\"" + Path.Combine(Paths.Root, "config") + "\"");
            }
            catch { }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string folder = FolderBox.Text == null ? "" : FolderBox.Text.Trim().Trim('"');
            if (folder.Length == 0)
            {
                MessageBox.Show("Pick a folder for downloads.", "Grappler",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Fail here, with a clear message, rather than at the start
                // of the next download.
                if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); }
            }
            catch (Exception ex)
            {
                MessageBox.Show("That folder could not be created:\n\n" + ex.Message,
                    "Grappler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.DownloadFolder = folder;
            _settings.MaxParallel = ParallelBox.SelectedIndex + 1;
            _settings.CookieBrowser = CookieBox.SelectedIndex <= 0
                ? "" : Browsers[CookieBox.SelectedIndex];
            _settings.EnsureFolders();
            _settings.CloseAction = CloseValues[Math.Max(0, CloseBox.SelectedIndex)];
            _settings.DefaultQuality = Math.Max(0, QualityBox.SelectedIndex);
            _settings.Save();

            DialogResult = true;
            Close();
        }
    }
}
