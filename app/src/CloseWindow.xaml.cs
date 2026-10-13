using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Grappler.Core;

namespace Grappler
{
    /// <summary>
    /// Asked when the window's close button is pressed, unless the user has
    /// already picked an answer for good. Escape or the title-bar X backs
    /// out without closing anything.
    /// </summary>
    public partial class CloseWindow : Window
    {
        /// <summary>AppSettings.CloseToTray or AppSettings.CloseQuits.</summary>
        public string Choice { get; private set; }

        public bool Remember { get { return RememberCheck.IsChecked == true; } }

        public CloseWindow(int activeDownloads)
        {
            InitializeComponent();

            if (activeDownloads > 0)
            {
                string count = activeDownloads == 1
                    ? "1 download is"
                    : activeDownloads.ToString(CultureInfo.InvariantCulture) + " downloads are";
                Body.Text = count + " still running. Quitting stops " +
                    (activeDownloads == 1 ? "it" : "them") + "; in the tray " +
                    (activeDownloads == 1 ? "it carries" : "they carry") +
                    " on, and the Brave extension keeps working.";
                QuitButton.Content = "Quit and stop";
            }

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { DialogResult = false; }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeChrome.Apply(this);
        }

        private void OnQuitClick(object sender, RoutedEventArgs e)
        {
            Choice = AppSettings.CloseQuits;
            DialogResult = true;
        }

        private void OnTrayClick(object sender, RoutedEventArgs e)
        {
            Choice = AppSettings.CloseToTray;
            DialogResult = true;
        }
    }
}
