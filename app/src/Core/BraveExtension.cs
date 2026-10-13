using System;
using System.IO;
using Microsoft.Win32;

namespace Grappler.Core
{
    /// <summary>
    /// Registers Grappler as the extension's "native messaging host" - the
    /// one program the browser-extension is allowed to start and talk to.
    ///
    /// It is a single registry value under HKEY_CURRENT_USER pointing at
    /// browser-extension\host\com.grappler.host.json: no admin rights, and
    /// the only thing Grappler ever writes outside its own folder. Chrome's
    /// key is set alongside Brave's - Brave builds have read it as well, and
    /// it makes the extension work in Chrome too.
    /// </summary>
    public static class BraveExtension
    {
        private const string HostName = "com.grappler.host";

        private static readonly string[] Keys = new string[]
        {
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName,
            @"Software\Google\Chrome\NativeMessagingHosts\" + HostName
        };

        public static bool IsConnected
        {
            get
            {
                foreach (string key in Keys)
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key))
                    {
                        if (k != null) { return true; }
                    }
                }
                return false;
            }
        }

        public static void Connect()
        {
            if (!File.Exists(Paths.HostManifest))
            {
                throw new FileNotFoundException("The browser-extension folder is missing from Grappler's folder.");
            }
            foreach (string key in Keys)
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("", Paths.HostManifest);
                }
            }
        }

        public static void Disconnect()
        {
            foreach (string key in Keys)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
                catch { }
            }
        }

        /// <summary>
        /// Called on every start: if the extension was set up and the
        /// Grappler folder has moved since, point the browser at the new
        /// place. Never registers anything that was not already there.
        /// </summary>
        public static void RefreshIfConnected()
        {
            try
            {
                foreach (string key in Keys)
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key, true))
                    {
                        if (k == null) { continue; }
                        string current = k.GetValue("") as string;
                        if (!string.Equals(current, Paths.HostManifest, StringComparison.OrdinalIgnoreCase))
                        {
                            k.SetValue("", Paths.HostManifest);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
