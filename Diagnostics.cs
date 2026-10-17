using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeskHardwareWidget
{
    public static class AppPaths
    {
        public static readonly string DataDirectory = FindDataDirectory();
        public static string ConfigPath { get { return Path.Combine(DataDirectory, "config.json"); } }
        public static string LogPath { get { return Path.Combine(DataDirectory, "widget.log"); } }

        private static string FindDataDirectory()
        {
            string portable = AppDomain.CurrentDomain.BaseDirectory;
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskHardwareWidget");
            // Elevation can make Program Files writable for the first time. Keep
            // using settings previously saved in LocalAppData by the same user.
            if (!File.Exists(Path.Combine(portable, "config.json")) && !File.Exists(Path.Combine(portable, "config.json.bak")) &&
                (File.Exists(Path.Combine(fallback, "config.json")) || File.Exists(Path.Combine(fallback, "config.json.bak"))))
                return fallback;
            string probe = Path.Combine(portable, ".write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return portable;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static class Diagnostics
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DateTime> LastMessages = new Dictionary<string, DateTime>();

        public static void Write(string message, Exception error)
        {
            lock (Sync)
            {
                DateTime last;
                if (LastMessages.TryGetValue(message, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                LastMessages[message] = DateTime.UtcNow;
                try
                {
                    if (File.Exists(AppPaths.LogPath) && new FileInfo(AppPaths.LogPath).Length > 1024 * 1024)
                        File.WriteAllText(AppPaths.LogPath, "", Encoding.UTF8);
                    File.AppendAllText(AppPaths.LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message +
                        (error == null ? "" : "\n" + error) + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
