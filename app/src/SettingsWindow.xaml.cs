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

                // yt-dlp says something like "yt-dlp is up to date" or
                // "Updated yt-dlp to ..." on the last non-empty line.
                string message = "Done";
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].Trim().Length > 0) { message = lines[i].Trim(); break; }
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    UpdateStatus.Text = message;
                    UpdateButton.IsEnabled = true;
                }));
            });
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
            _settings.Save();

            DialogResult = true;
            Close();
        }
    }
}
