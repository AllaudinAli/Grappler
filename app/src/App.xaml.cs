using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Grappler.Core;

namespace Grappler
{
    public partial class App : Application
    {
        /// <summary>Start with no window, just the tray icon - how the Brave extension starts it.</summary>
        public const string TrayArg = "--tray";

        // Held for the life of the process; only its existence matters.
        private static Mutex _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Started by Brave on behalf of the extension: pass the request
            // to the real window and exit without showing anything. This is
            // why the window is created below rather than via StartupUri.
            if (BrowserBridge.IsHostLaunch(e.Args))
            {
                Shutdown(BrowserBridge.RunHost());
                return;
            }

            // One Grappler at a time - two would download into the same
            // folder and fight over the extension. Opening it again just
            // brings the running one forward (from the tray, if hidden).
            bool startHidden = Array.IndexOf(e.Args, TrayArg) >= 0;
            bool first;
            _singleInstance = new Mutex(true, @"Local\Grappler.App." + Environment.UserName, out first);
            if (!first)
            {
                if (!startHidden) { BrowserBridge.ShowRunningApp(); }
                Shutdown(0);
                return;
            }

            // Small text is the whole interface here, so opt every window
            // into Display-mode formatting - GDI-compatible metrics keep
            // 11-13px type from going soft the way Ideal mode makes it.
            TextOptions.TextFormattingModeProperty.OverrideMetadata(
                typeof(Window),
                new FrameworkPropertyMetadata(TextFormattingMode.Display,
                    FrameworkPropertyMetadataOptions.Inherits));

            // A crash inside a background download must not take the whole
            // app down with it; surface it and carry on.
            DispatcherUnhandledException += delegate(object s,
                System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
            {
                MessageBox.Show(
                    args.Exception.Message,
                    "Grappler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                args.Handled = true;
            };

            base.OnStartup(e);

            // Closing the window may only hide it to the tray, so the app
            // ends when MainWindow says so, not when no window is showing.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MainWindow window = new MainWindow();
            if (!startHidden) { window.Show(); }
        }
    }
}
