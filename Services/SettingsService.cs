using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FileSorter.Models;

namespace FileSorter.Services;

public static class SettingsService
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileSorter");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    private static AppSettings? _cached;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            // Persist enums by name (e.g. "Suffix", "ByYear") so the JSON stays readable
            // and forward-compatible if we ever reorder enum members.
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                if (s != null && s.Categories.Count > 0)
                {
                    if (Migrate(s))
                    {
                        try { Save(s); } catch { /* not fatal */ }
                    }
                    _cached = s;
                    return s;
                }
            }
        }
        catch
        {
            // Corrupt file? Fall through to defaults; user can edit / re-save.
        }

        var defaults = AppSettings.CreateDefault();
        _cached = defaults;
        try { Save(defaults); } catch { /* read-only filesystem? not fatal */ }
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(SettingsPath, json);
        _cached = settings;
    }

    public static AppSettings Cached => _cached ??= Load();

    public static void SetCached(AppSettings s) => _cached = s;

    /// <summary>
    /// One-shot migration of older settings.json files to the new category layout.
    /// Conservative: only changes fields that still match the previous default values
    /// — anything user-edited is left alone.
    /// Returns true if anything changed.
    /// </summary>
    private static bool Migrate(AppSettings s)
    {
        var changed = false;

        // images: "Фото" → "Изображения"
        var images = s.Categories.Find(c => c.Id == "images");
        if (images != null && images.NameRu == "Фото")
        {
            images.NameRu = "Изображения";
            changed = true;
        }

        // installers: split .exe into a new "programs" category if not present yet
        var installers = s.Categories.Find(c => c.Id == "installers");
        var programs = s.Categories.Find(c => c.Id == "programs");
        if (installers != null && programs == null && installers.Extensions.Contains(".exe"))
        {
            installers.Extensions.RemoveAll(e =>
                string.Equals(e, ".exe", StringComparison.OrdinalIgnoreCase));

            var newPrograms = new Category
            {
                Id = "programs",
                NameRu = "Программы",
                NameEn = "Programs",
                Extensions = new List<string> { ".exe" }
            };
            // Insert right after installers so the order stays sane in the editor.
            var idx = s.Categories.IndexOf(installers);
            s.Categories.Insert(idx + 1, newPrograms);
            changed = true;
        }

        return changed;
    }
}
