using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Grappler.Core
{
    /// <summary>
    /// Keeps the system title bar but tells DWM to draw it dark, so there
    /// is no white strip sitting on top of a dark window. Using the real
    /// title bar (rather than faking one) keeps Snap, the window menu and
    /// accessibility behaving exactly as Windows expects.
    /// </summary>
    public static class NativeChrome
    {
        // 20 on Windows 10 2004+ and Windows 11; 19 on the earlier builds
        // that shipped it as an undocumented attribute.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int valueSize);

        public static void Apply(Window window)
        {
            if (window == null) { return; }

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) { return; }

            int enabled = 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode,
                                      ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy,
                                      ref enabled, sizeof(int));
            }

            // Ignored below Windows 11, which is fine - it is the default
            // shape there anyway.
            int corner = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference,
                                  ref corner, sizeof(int));
        }
    }
}
