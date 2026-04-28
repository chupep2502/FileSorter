using System.Collections.Generic;

namespace FileSorter.Models;

/// <summary>How collisions in the destination folder are resolved.</summary>
public enum CollisionStrategy
{
    /// <summary>Append " (1)", " (2)", ... — never overwrites. Default.</summary>
    Suffix,
    /// <summary>Skip the file silently if a same-named file already exists.</summary>
    Skip,
    /// <summary>Overwrite if the source has a newer LastWriteTime, else skip.</summary>
    ReplaceIfNewer,
    /// <summary>Show a per-file dialog with Apply-to-All option.</summary>
    Ask
}

/// <summary>What "category" means: file extension or modification date bucket.</summary>
public enum SortMode
{
    /// <summary>Default: bucket by file extension via Categories list.</summary>
    ByExtension,
    /// <summary>Bucket by LastWriteTime year (e.g. "2024").</summary>
    ByYear,
    /// <summary>Bucket by LastWriteTime year/month (e.g. "2024-03").</summary>
    ByYearMonth,
    /// <summary>Bucket by extension category, then year inside (e.g. "Документы\\2024").</summary>
    ByExtensionAndYear
}

/// <summary>
/// All persisted user settings. Serialized to %APPDATA%\FileSorter\settings.json.
/// </summary>
public class AppSettings
{
    public string Language { get; set; } = "ru";
    /// <summary>"system" | "light" | "dark".</summary>
    public string Theme { get; set; } = "system";
    public bool Recursive { get; set; } = false;
    public bool DryRunByDefault { get; set; } = false;
    public bool CreateOtherFolder { get; set; } = true;
    public string OtherNameRu { get; set; } = "Прочее";
    public string OtherNameEn { get; set; } = "Other";

    public CollisionStrategy CollisionStrategy { get; set; } = CollisionStrategy.Suffix;
    public SortMode SortMode { get; set; } = SortMode.ByExtension;

    public List<string> SkipExtensions { get; set; } = new() { ".lnk", ".url", ".ini" };
    public List<string> RecentFolders { get; set; } = new();
    public List<Category> Categories { get; set; } = new();

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            Categories = new List<Category>
            {
                new()
                {
                    Id = "documents",
                    NameRu = "Документы",
                    NameEn = "Documents",
                    Extensions = new List<string>
                    {
                        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                        ".txt", ".rtf", ".odt", ".ods", ".odp", ".csv", ".md",
                        ".epub", ".mobi", ".djvu"
                    }
                },
                new()
                {
                    Id = "images",
                    NameRu = "Изображения",
                    NameEn = "Images",
                    Extensions = new List<string>
                    {
                        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff",
                        ".webp", ".svg", ".ico", ".heic", ".raw", ".psd"
                    }
                },
                new()
                {
                    Id = "videos",
                    NameRu = "Видео",
                    NameEn = "Videos",
                    Extensions = new List<string>
                    {
                        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
                        ".m4v", ".mpg", ".mpeg", ".3gp"
                    }
                },
                new()
                {
                    Id = "audio",
                    NameRu = "Аудио",
                    NameEn = "Audio",
                    Extensions = new List<string>
                    {
                        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a",
                        ".opus", ".aiff"
                    }
                },
                new()
                {
                    Id = "archives",
                    NameRu = "Архивы",
                    NameEn = "Archives",
                    Extensions = new List<string>
                    {
                        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
                        ".iso", ".cab"
                    }
                },
                new()
                {
                    Id = "installers",
                    NameRu = "Установщики",
                    NameEn = "Installers",
                    Extensions = new List<string>
                    {
                        ".msi", ".msix", ".appx", ".appxbundle"
                    }
                },
                new()
                {
                    Id = "programs",
                    NameRu = "Программы",
                    NameEn = "Programs",
                    Extensions = new List<string>
                    {
                        ".exe"
                    }
                },
                new()
                {
                    Id = "code",
                    NameRu = "Код",
                    NameEn = "Code",
                    Extensions = new List<string>
                    {
                        ".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".htm",
                        ".css", ".json", ".xml", ".yaml", ".yml", ".sh", ".ps1",
                        ".bat", ".cmd", ".c", ".cpp", ".h", ".hpp", ".cs",
                        ".java", ".go", ".rs", ".rb", ".php", ".sql", ".ipynb"
                    }
                }
            }
        };
    }

    /// <summary>Adds a folder to recent list (deduped, capped at 8, most recent first).</summary>
    public void TouchRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentFolders.RemoveAll(p => string.Equals(p, path, System.StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, path);
        while (RecentFolders.Count > 8) RecentFolders.RemoveAt(RecentFolders.Count - 1);
    }
}
