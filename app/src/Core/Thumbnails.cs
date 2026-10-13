using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Grappler.Core
{
    /// <summary>
    /// Fetches and decodes card thumbnails on background threads.
    ///
    /// Two things here matter for memory: DecodePixelWidth means a 1280px
    /// JPEG is decoded straight to card size rather than held full-size and
    /// scaled at draw time, and Freeze() makes the result immutable so the
    /// same bitmap can be handed to the UI thread without a copy or a lock.
    /// A 50-item queue costs a few MB instead of a few hundred.
    /// </summary>
    public static class Thumbnails
    {
        // Twice the 112px card slot, so it stays sharp at 200% display scaling.
        private const int DecodeWidth = 224;

        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        private static readonly object CacheGate = new object();

        static Thumbnails()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
            ServicePointManager.DefaultConnectionLimit = 8;
        }

        /// <summary>Background thread. Returns a frozen image, or null.</summary>
        public static ImageSource Get(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            lock (CacheGate)
            {
                ImageSource hit;
                if (Cache.TryGetValue(url, out hit)) { return hit; }
            }

            ImageSource image = null;
            try
            {
                byte[] bytes = Download(url);
                if (bytes != null && bytes.Length > 0)
                {
                    image = Decode(bytes);

                    // YouTube serves .webp thumbnails, which WPF cannot
                    // decode. ffmpeg is already sitting in bin\, so use it
                    // rather than dropping the image or special-casing
                    // YouTube URLs.
                    if (image == null) { image = DecodeViaFfmpeg(bytes); }
                }
            }
            catch { }

            if (image != null)
            {
                lock (CacheGate) { Cache[url] = image; }
            }
            return image;
        }

        private static byte[] Download(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.UserAgent = "Mozilla/5.0";
            request.AllowAutoRedirect = true;

            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                if (stream == null) { return null; }
                byte[] chunk = new byte[16384];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    // A thumbnail is never this big; anything that is, is
                    // not a thumbnail.
                    if (buffer.Length > 8 * 1024 * 1024) { break; }
                }
                return buffer.ToArray();
            }
        }

        private static ImageSource Decode(byte[] bytes)
        {
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // release the stream at EndInit
                bitmap.DecodePixelWidth = DecodeWidth;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource DecodeViaFfmpeg(byte[] bytes)
        {
            if (!File.Exists(Paths.Ffmpeg)) { return null; }

            string stem = Path.Combine(Path.GetTempPath(),
                "grappler-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            string inPath = stem + ".bin";
            string outPath = stem + ".png";

            try
            {
                File.WriteAllBytes(inPath, bytes);

                List<string> args = new List<string>();
                args.Add("-v"); args.Add("quiet");
                args.Add("-i"); args.Add(inPath);
                args.Add("-frames:v"); args.Add("1");
                args.Add("-vf"); args.Add("scale=" + DecodeWidth + ":-1");
                args.Add("-y"); args.Add(outPath);

                ProcessStartInfo psi = new ProcessStartInfo(
                    Paths.Ffmpeg, YtDlp.CommandLine(args));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(15000))
                    {
                        ProcessTree.Kill(p);
                        return null;
                    }
                }

                if (!File.Exists(outPath)) { return null; }
                return Decode(File.ReadAllBytes(outPath));
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(inPath)) { File.Delete(inPath); } } catch { }
                try { if (File.Exists(outPath)) { File.Delete(outPath); } } catch { }
            }
        }
    }
}
