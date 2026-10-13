using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Grappler.Core
{
    /// <summary>One link sent from the Brave extension.</summary>
    public sealed class BrowserRequest
    {
        public string Url;

        /// <summary>Index into QualityPreset.All, or -1 for whatever the window has selected.</summary>
        public int Quality = -1;

        /// <summary>The site's cookies as cookies.txt text, or null.</summary>
        public string Cookies;

        public static BrowserRequest FromMessage(Dictionary<string, object> m)
        {
            BrowserRequest r = new BrowserRequest();
            r.Url = BrowserBridge.Str(m, "url");
            r.Cookies = BrowserBridge.Str(m, "cookies");
            r.Quality = BrowserBridge.Int(m, "quality", -1);
            return r;
        }
    }

    /// <summary>
    /// Connects the Brave extension (browser-extension\) to the running app.
    ///
    /// Brave cannot talk to a window, only to a program it starts itself
    /// and exchanges messages with over stdin/stdout ("native messaging").
    /// So Brave starts Grappler.exe with its extension origin as the first
    /// argument; in that mode no window opens - each request is handed to
    /// the real Grappler over a named pipe, and the answer goes back.
    ///
    ///   extension  --stdin-->  Grappler.exe (host)  --pipe-->  Grappler (tray)
    ///
    /// A grab starts Grappler first if needed, hidden in the tray. Status
    /// and row actions never start it - an empty list is the right answer
    /// when nothing is running.
    ///
    /// The popup keeps one host alive for as long as it is open and polls
    /// through it, so the host reads messages until Brave closes stdin.
    ///
    /// Which extension may start the host is decided by allowed_origins in
    /// browser-extension\host\com.grappler.host.json, not here.
    /// </summary>
    public static class BrowserBridge
    {
        private const string OriginPrefix = "chrome-extension://";
        private static readonly string PipeName = "Grappler.Browser." + Environment.UserName;
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            json.MaxJsonLength = 64 * 1024 * 1024;   // cookies for a busy site add up
            return json;
        }

        public static bool IsHostLaunch(string[] args)
        {
            if (args == null) { return false; }
            foreach (string arg in args)
            {
                if (arg.StartsWith(OriginPrefix, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        // ================= host side (started by Brave) ===================
        public static int RunHost()
        {
            try
            {
                Stream stdin = Console.OpenStandardInput();
                Stream stdout = Console.OpenStandardOutput();

                string message;
                while ((message = ReadMessage(stdin)) != null)
                {
                    string reply;
                    try { reply = HandleHostMessage(message); }
                    catch (Exception ex) { reply = Reply(ex.Message); }
                    WriteMessage(stdout, reply);
                }
                return 0;
            }
            catch { return 1; }
        }

        private static string HandleHostMessage(string message)
        {
            Dictionary<string, object> m = Json.DeserializeObject(message) as Dictionary<string, object>;
            string type = Str(m, "type");

            switch (type)
            {
                case "ping":
                    return Reply(Paths.IsInstalled ? null : "yt-dlp.exe is missing from Grappler's bin folder.");

                case "grab":
                    return Forward(message, true) ?? Reply("Grappler did not start in time.");

                case "show":
                    // Brave is in the foreground; pass that on so the window
                    // can actually come to the front instead of just flashing.
                    AllowSetForegroundWindow(AsfwAny);
                    return Forward(message, true) ?? Reply("Grappler did not start in time.");

                case "status":
                    return Forward(message, false) ?? "{\"ok\":true,\"running\":false,\"items\":[]}";

                case "action":
                    return Forward(message, false) ?? Reply("Grappler isn't running.");

                default:
                    return Reply("Unknown request.");
            }
        }

        /// <summary>Passes one message to the app and returns its answer, or
        /// null if the app is not running (and launch is false).</summary>
        private static string Forward(string message, bool launch)
        {
            NamedPipeClientStream pipe = Connect(launch ? 400 : 150);
            if (pipe == null && launch)
            {
                LaunchApp();
                pipe = Connect(20000);
            }
            if (pipe == null) { return null; }

            using (pipe)
            {
                StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false));
                StreamReader reader = new StreamReader(pipe, Encoding.UTF8);
                // JSON.stringify never emits raw newlines, so one message is one line.
                writer.WriteLine(message);
                writer.Flush();
                return reader.ReadLine() ?? Reply("Grappler closed the connection.");
            }
        }

        private static NamedPipeClientStream Connect(int timeoutMs)
        {
            NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            try
            {
                pipe.Connect(timeoutMs);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Starts Grappler hidden in the tray. Created through WMI rather
        /// than as a child of this host: Brave tears the host down once the
        /// extension is done with it, and the app - with every download in
        /// it - must not go down too. WMI is also the one detached route
        /// that still lets "--tray" through (explorer.exe drops arguments).
        /// </summary>
        private static void LaunchApp()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                using (ManagementClass processes = new ManagementClass("Win32_Process"))
                {
                    ManagementBaseObject input = processes.GetMethodParameters("Create");
                    input["CommandLine"] = "\"" + exe + "\" " + App.TrayArg;
                    input["CurrentDirectory"] = Path.GetDirectoryName(exe);
                    ManagementBaseObject output = processes.InvokeMethod("Create", input, null);
                    if (output != null && Convert.ToInt32(output["ReturnValue"]) == 0) { return; }
                }
            }
            catch { }

            ProcessStartInfo psi = new ProcessStartInfo(exe, App.TrayArg);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }

        // Native messaging framing: a 4-byte native-endian length, then UTF-8 JSON.
        private static string ReadMessage(Stream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            if (header == null) { return null; }
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > 64 * 1024 * 1024) { return null; }
            byte[] body = ReadExactly(stream, length);
            return body == null ? null : Encoding.UTF8.GetString(body);
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) { return null; }
                read += n;
            }
            return buffer;
        }

        private static void WriteMessage(Stream stream, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            stream.Write(BitConverter.GetBytes(body.Length), 0, 4);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        // ================= app side ======================================
        /// <summary>
        /// Listens for the host on background threads. The handler gets the
        /// parsed message and returns the reply; it runs off the UI thread,
        /// so marshalling is the caller's job. Several connections are served
        /// at once, so a format dialog left open does not stall the popup.
        /// </summary>
        public static void Listen(Func<Dictionary<string, object>, Dictionary<string, object>> handler)
        {
            Thread thread = new Thread(delegate() { AcceptLoop(handler); });
            thread.IsBackground = true;   // never keeps the app alive on close
            thread.Name = "Browser bridge";
            thread.Start();
        }

        private static void AcceptLoop(Func<Dictionary<string, object>, Dictionary<string, object>> handler)
        {
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances);
                }
                catch (IOException) { return; }

                try { server.WaitForConnection(); }
                catch
                {
                    server.Dispose();
                    Thread.Sleep(200);
                    continue;
                }

                NamedPipeServerStream connected = server;
                ThreadPool.QueueUserWorkItem(delegate { Serve(connected, handler); });
            }
        }

        private static void Serve(NamedPipeServerStream server,
                                  Func<Dictionary<string, object>, Dictionary<string, object>> handler)
        {
            using (server)
            {
                try
                {
                    StreamReader reader = new StreamReader(server, Encoding.UTF8);
                    StreamWriter writer = new StreamWriter(server, new UTF8Encoding(false));

                    Dictionary<string, object> message = null;
                    try { message = Json.DeserializeObject(reader.ReadLine() ?? "") as Dictionary<string, object>; }
                    catch { }

                    string reply;
                    if (message == null)
                    {
                        reply = Reply("Unreadable request.");
                    }
                    else
                    {
                        try { reply = Json.Serialize(handler(message)); }
                        catch (Exception ex) { reply = Reply(ex.Message); }
                    }

                    writer.WriteLine(reply);
                    writer.Flush();
                    server.WaitForPipeDrain();
                }
                catch { }
            }
        }

        /// <summary>
        /// For a second copy of Grappler: asks the one already running to
        /// show its window. False if nothing answered.
        /// </summary>
        public static bool ShowRunningApp()
        {
            AllowSetForegroundWindow(AsfwAny);
            NamedPipeClientStream pipe = Connect(3000);
            if (pipe == null) { return false; }
            using (pipe)
            {
                StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false));
                StreamReader reader = new StreamReader(pipe, Encoding.UTF8);
                writer.WriteLine("{\"type\":\"show\"}");
                writer.Flush();
                try { return reader.ReadLine() != null; }
                catch { return false; }
            }
        }

        // ================= cookies =======================================
        // Written per download into the temp folder, used with --cookies,
        // and deleted when the row is cleared or the app closes. They are
        // live sign-in sessions, so they never outlive the app.
        private static string CookieDir(AppSettings settings)
        {
            return Path.Combine(settings.TempFolder, "cookies");
        }

        public static string SaveCookies(AppSettings settings, string text)
        {
            if (string.IsNullOrEmpty(text)) { return null; }
            string dir = CookieDir(settings);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public static void DeleteCookies(string path)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            try { File.Delete(path); }
            catch { }
        }

        public static void DeleteAllCookies(AppSettings settings)
        {
            try
            {
                string dir = CookieDir(settings);
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
            }
            catch { }
        }

        // ================= helpers =======================================
        public static Dictionary<string, object> Result(string error)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["ok"] = error == null;
            if (error != null) { r["error"] = error; }
            return r;
        }

        private static string Reply(string error)
        {
            return Json.Serialize(Result(error));
        }

        public static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            if (m == null || !m.TryGetValue(key, out v)) { return null; }
            return v as string;
        }

        public static int Int(Dictionary<string, object> m, string key, int fallback)
        {
            object v;
            if (m == null || !m.TryGetValue(key, out v)) { return fallback; }
            return v is int ? (int)v : fallback;
        }

        private const int AsfwAny = -1;

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
