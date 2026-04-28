using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileSorter.Services;

/// <summary>One row in a sort journal: a single move from src→dest.</summary>
public class JournalEntry
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
}

/// <summary>Whole journal — one per sort run that actually moved files.</summary>
public class Journal
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Folder { get; set; } = "";
    public List<JournalEntry> Entries { get; set; } = new();
}

/// <summary>
/// Persistent journal of every move performed (one file per sort run) so the user
/// can undo the last sort. Stored in %APPDATA%\FileSorter\history\YYYY-MM-DD_HH-mm-ss.json.
/// Older runs are pruned to the last 10.
/// </summary>
public static class JournalService
{
    private const int Keep = 10;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string HistoryDir =>
        Path.Combine(SettingsService.AppDataDir, "history");

    /// <summary>Save a journal; returns the path it was written to (null on failure).</summary>
    public static string? Save(Journal journal)
    {
        try
        {
            Directory.CreateDirectory(HistoryDir);
            var name = journal.Timestamp.ToString("yyyy-MM-dd_HH-mm-ss") + ".json";
            var path = Path.Combine(HistoryDir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(journal, _jsonOptions));
            Prune();
            return path;
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save undo journal: " + ex.Message);
            return null;
        }
    }

    /// <summary>Newest journal file, or null if there is none.</summary>
    public static string? GetLatestPath()
    {
        try
        {
            if (!Directory.Exists(HistoryDir)) return null;
            return Directory.EnumerateFiles(HistoryDir, "*.json")
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .FirstOrDefault();
        }
        catch { return null; }
    }

    public static Journal? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Journal>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load journal: " + ex.Message);
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private static void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(HistoryDir, "*.json")
                                 .OrderByDescending(f => File.GetLastWriteTime(f))
                                 .ToList();
            for (int i = Keep; i < files.Count; i++)
            {
                try { File.Delete(files[i]); } catch { /* best effort */ }
            }
        }
        catch { /* ignore */ }
    }
}
