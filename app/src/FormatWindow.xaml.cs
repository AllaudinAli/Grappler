using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Grappler.Core;

namespace Grappler
{
    public partial class FormatWindow : Window
    {
        private readonly string _url;
        private readonly AppSettings _settings;

        /// <summary>The ID the user settled on, once the dialog says OK.</summary>
        public string ChosenFormat { get; private set; }

        public FormatWindow(string url, AppSettings settings)
        {
            _url = url;
            _settings = settings;
            InitializeComponent();

            UrlText.Text = url;
            ChosenFormat = "";

            Loaded += delegate { LoadFormats(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeChrome.Apply(this);
        }

        private void LoadFormats()
        {
            Task.Factory.StartNew(delegate
            {
                string[] lines = YtDlp.Formats(_url, _settings);

                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;

                    if (lines.Length == 0)
                    {
                        FormatList.ItemsSource = new string[]
                        {
                            "Could not read the formats for this link.",
                            "",
                            "It may be private, region-locked, or need a sign-in -",
                            "see Sign-in cookies in Settings."
                        };
                        DownloadButton.IsEnabled = false;
                        return;
                    }

                    FormatList.ItemsSource = TableOnly(lines);
                    FormatIdBox.Focus();
                }));
            });
        }

        /// <summary>
        /// yt-dlp narrates itself on stdout before printing the table
        /// ("Downloading webpage", "Solving JS challenges", ...). Useful in
        /// a terminal, pure noise above a list you are meant to pick from -
        /// so start at the column header and keep the table verbatim.
        /// </summary>
        private static string[] TableOnly(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("ID ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("ID\t", StringComparison.Ordinal))
                {
                    string[] table = new string[lines.Length - i];
                    Array.Copy(lines, i, table, 0, table.Length);
                    return table;
                }
            }
            return lines;   // unrecognised layout: show everything rather than nothing
        }

        private void OnRowSelected(object sender, SelectionChangedEventArgs e)
        {
            string id = IdFromRow(FormatList.SelectedItem as string);
            if (id != null) { FormatIdBox.Text = id; }
        }

        private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (IdFromRow(FormatList.SelectedItem as string) != null) { Accept(); }
        }

        /// <summary>
        /// The first token of a table row is the format ID. Header and
        /// separator lines start with "[" or a dash, so they are ignored -
        /// clicking one should not put rubbish in the box.
        /// </summary>
        private static string IdFromRow(string row)
        {
            if (string.IsNullOrEmpty(row)) { return null; }

            string trimmed = row.Trim();
            if (trimmed.Length == 0) { return null; }
            if (trimmed[0] == '[' || trimmed[0] == '-') { return null; }

            int space = trimmed.IndexOf(' ');
            string token = space < 0 ? trimmed : trimmed.Substring(0, space);
            if (token.Length == 0 || token.Length > 40) { return null; }

            // "ID" is the column heading, not a format.
            if (string.Equals(token, "ID", StringComparison.Ordinal)) { return null; }

            foreach (char c in token)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '+';
                if (!ok) { return null; }
            }
            return token;
        }

        private void OnDownloadClick(object sender, RoutedEventArgs e) { Accept(); }

        private void Accept()
        {
            string id = FormatIdBox.Text == null ? "" : FormatIdBox.Text.Trim();
            if (id.Length == 0)
            {
                FormatIdBox.Focus();
                return;
            }
            ChosenFormat = id;
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
