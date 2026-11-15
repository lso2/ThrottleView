using System;
using System.IO;

namespace ThrottleView.Services
{
    /// <summary>
    /// Plain file logger. Writes to %APPDATA%\ThrottleView\debug.log, falling back to
    /// %TEMP%\throttleview.log when APPDATA is not writable. It never touches the Windows Event Log,
    /// and logging never throws, so a logging failure can never take the app down.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path { get { return _path; } }

        public static void Init()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThrottleView");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "debug.log");
                RollIfLarge();
            }
            catch
            {
                _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "throttleview.log");
            }
        }

        public static void Info(string message) { Write("INFO ", message); }

        public static void Error(string message) { Write("WARN ", message); }

        public static void Error(string message, Exception ex)
        {
            Write("WARN ", message + " :: " + ex.GetType().Name + ": " + ex.Message
                  + Environment.NewLine + ex.StackTrace);
        }

        private static void Write(string level, string message)
        {
            if (_path == null) _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "throttleview.log");
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            try { lock (Gate) { File.AppendAllText(_path, line + Environment.NewLine); } }
            catch { /* never crash on logging */ }
        }

        private static void RollIfLarge()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    var bak = _path + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_path, bak);
                }
            }
            catch { }
        }
    }
}
