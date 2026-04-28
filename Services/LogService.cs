using System;
using System.IO;
using System.Text;

namespace FileSorter.Services;

/// <summary>
/// Append-only daily log file at %APPDATA%\FileSorter\logs\YYYY-MM-DD.log.
/// Best-effort: failures are swallowed so the UI never crashes on disk problems.
/// Thread-safe via a single lock — log volume is tiny (one line per file moved).
/// </summary>
public static class LogService
{
    private static readonly object _gate = new();

    public static string LogsDir =>
        Path.Combine(SettingsService.AppDataDir, "logs");

    private static string CurrentLogPath =>
        Path.Combine(LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    /// <summary>Write a single timestamped line to today's log file.</summary>
    public static void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(LogsDir);
                var line = string.Format("{0:HH:mm:ss} [{1}] {2}{3}",
                                         DateTime.Now, level, message, Environment.NewLine);
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void Info (string m) => Write("INFO",  m);
    public static void Move (string m) => Write("MOVE",  m);
    public static void Skip (string m) => Write("SKIP",  m);
    public static void Error(string m) => Write("ERROR", m);

    /// <summary>Garbage-collect log files older than <paramref name="keepDays"/>.</summary>
    public static void CleanupOld(int keepDays = 30)
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(LogsDir, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
                catch { /* ignore individual failures */ }
            }
        }
        catch { /* ignore */ }
    }
}
