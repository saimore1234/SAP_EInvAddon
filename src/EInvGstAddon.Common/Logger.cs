using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EInvGstAddon.Common
{
    public enum LogLevel { Debug, Info, Warn, Error }

    /// <summary>Thread-safe daily-rolling file logger. Registered secrets are masked in every line.</summary>
    public static class Logger
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Secrets = new List<string>();
        private static string _dir;
        public static LogLevel Min = LogLevel.Info;

        public static string Directory
        {
            get
            {
                if (_dir == null)
                    _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EInvGstAddon", "logs");
                return _dir;
            }
            set { _dir = value; }
        }

        public static void RegisterSecret(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 4) return;
            lock (Gate) { if (!Secrets.Contains(s)) Secrets.Add(s); }
        }

        public static string Mask(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            lock (Gate) { foreach (var s in Secrets) text = text.Replace(s, "****"); }
            return text;
        }

        public static void Debug(string ev, params object[] kv) { Write(LogLevel.Debug, ev, null, kv); }
        public static void Info(string ev, params object[] kv) { Write(LogLevel.Info, ev, null, kv); }
        public static void Warn(string ev, params object[] kv) { Write(LogLevel.Warn, ev, null, kv); }
        public static void Error(string ev, Exception ex, params object[] kv) { Write(LogLevel.Error, ev, ex, kv); }

        private static void Write(LogLevel lvl, string ev, Exception ex, object[] kv)
        {
            if (lvl < Min) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(lvl.ToString().ToUpperInvariant().PadRight(5)).Append(' ').Append(ev);
                for (int i = 0; kv != null && i + 1 < kv.Length; i += 2)
                    sb.Append(' ').Append(kv[i]).Append('=').Append(kv[i + 1]);
                if (ex != null) sb.Append(" ex=").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                string line = Mask(sb.ToString());
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(Path.Combine(Directory, "einvgst-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine);
                }
            }
            catch { /* logging must never break the add-on */ }
        }
    }
}
