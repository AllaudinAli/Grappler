using System;
using System.Drawing;
using System.Windows.Forms;

namespace Grappler.Core
{
    /// <summary>
    /// The notification-area icon. WPF has none of its own, so this wraps
    /// the WinForms one - already referenced for the folder picker.
    ///
    /// Shown for as long as the app runs, whether the window is open or
    /// not, so there is always one obvious way to see what is downloading
    /// or to quit.
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private Action _balloonClick;

        public event EventHandler OpenRequested;
        public event EventHandler FolderRequested;
        public event EventHandler QuitRequested;

        public TrayIcon()
        {
            _icon = new NotifyIcon();
            _icon.Text = "Grappler";
            try { _icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _icon.Icon = SystemIcons.Application; }

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripItem open = menu.Items.Add("Open Grappler", null, delegate { Raise(OpenRequested); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add("Open downloads folder", null, delegate { Raise(FolderRequested); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Raise(QuitRequested); });
            _icon.ContextMenuStrip = menu;

            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { Raise(OpenRequested); }
            };
            _icon.BalloonTipClicked += delegate
            {
                Action click = _balloonClick;
                _balloonClick = null;
                if (click != null) { click(); }
            };
            _icon.BalloonTipClosed += delegate { _balloonClick = null; };

            _icon.Visible = true;
        }

        /// <summary>The hover text: a summary of what is running.</summary>
        public void SetTooltip(string text)
        {
            // NotifyIcon throws above 63 characters.
            if (text.Length > 63) { text = text.Substring(0, 60) + "..."; }
            _icon.Text = text;
        }

        /// <summary>A Windows notification; onClick runs if it is clicked.</summary>
        public void Notify(string title, string text, bool isError, Action onClick)
        {
            _balloonClick = onClick;
            _icon.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text,
                isError ? ToolTipIcon.Warning : ToolTipIcon.None);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) { handler(this, EventArgs.Empty); }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
