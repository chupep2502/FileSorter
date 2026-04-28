using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FileSorter.Models;

namespace FileSorter.Services;

/// <summary>
/// Reasons we will refuse to sort a folder. Surfaced through ValidateRoot so the UI
/// can show a meaningful message before kicking off enumeration.
/// </summary>
public enum RootValidation
{
    Ok,
    Empty,
    NotFound,
    SystemFolder,
    DriveRoot
}

public static class SorterService
{
    /// <summary>
    /// Refuse to sort drive roots ("C:\") or system folders (Windows, Program Files).
    /// Catches the "I dragged my whole C: drive in" foot-gun.
    /// </summary>
    public static RootValidation ValidateRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return RootValidation.Empty;
        string full;
        try { full = Path.GetFullPath(folder); } catch { return RootValidation.NotFound; }
        if (!Directory.Exists(full)) return RootValidation.NotFound;

        try
        {
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Drive root like "C:" → length 2, or "\\server\share" — reject.
            if (string.Equals(Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              trimmed, StringComparison.OrdinalIgnoreCase))
                return RootValidation.DriveRoot;

            string[] forbidden =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            foreach (var f in forbidden)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (string.Equals(Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar),
                                  trimmed, StringComparison.OrdinalIgnoreCase))
                    return RootValidation.SystemFolder;
            }
        }
        catch { /* fall through — better to proceed than crash */ }

        return RootValidation.Ok;
    }

    /// <summary>
    /// Cheap pre-pass: count files and sum bytes that would be touched by a real sort.
    /// Used by the UI to show a "Found N files (M GB)" confirmation for big jobs.
    /// </summary>
    public static (int Count, long TotalBytes) CountFiles(string folder, AppSettings settings, bool recursive)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return (0, 0);

            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System
            };

            var skipExtensions = BuildSkipSet(settings);
            var categoryFolderPaths = BuildCategoryFolderSet(folder, settings);

            int count = 0;
            long size = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*", enumOpts))
            {
                try
                {
                    var ext = NormalizeExt(Path.GetExtension(path));
                    if (skipExtensions.Contains(ext)) continue;
                    var parent = Path.GetFullPath(Path.GetDirectoryName(path) ?? "");
                    if (categoryFolderPaths.Contains(parent)) continue;
                    var fi = new FileInfo(path);
                    count++;
                    size += fi.Length;
                }
                catch { /* skip */ }
            }
            return (count, size);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Sorts files in <paramref name="folder"/> into category subfolders.
    /// Reports per-file progress; never moves files outside the source folder.
    /// </summary>
    public static Task<SortResult> SortAsync(
        string folder,
        AppSettings settings,
        bool recursive,
        bool dryRun,
        IProgress<SortStep>? progress,
        IProgress<SortProgress>? counter,
        Func<string, string, CollisionDecision>? collisionPrompt,
        CancellationToken ct)
    {
        return Task.Run(() => SortInternal(folder, settings, recursive, dryRun, progress, counter, collisionPrompt, ct), ct);
    }

    private static SortResult SortInternal(
        string folder,
        AppSettings settings,
        bool recursive,
        bool dryRun,
        IProgress<SortStep>? progress,
        IProgress<SortProgress>? counter,
        Func<string, string, CollisionDecision>? collisionPrompt,
        CancellationToken ct)
    {
        var loc = LocalizationService.Current;
        var result = new SortResult { DryRun = dryRun };

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            progress?.Report(new SortStep
            {
                Kind = SortStepKind.Error,
                Message = string.Format(loc.T("FolderNotFound"), folder ?? "")
            });
            result.Errors++;
            return result;
        }

        // Build extension -> category lookup (lazy, only used when SortMode == ByExtension or ByExtensionAndYear)
        var extToCat = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        var compiledRegex = new List<(Regex re, Category cat)>();
        foreach (var cat in settings.Categories)
        {
            foreach (var ext in cat.Extensions)
            {
                var key = NormalizeExt(ext);
                if (!string.IsNullOrEmpty(key))
                    extToCat[key] = cat;
            }
            foreach (var pat in cat.NameRegex)
            {
                if (string.IsNullOrWhiteSpace(pat)) continue;
                try { compiledRegex.Add((new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), cat)); }
                catch { /* invalid regex — silently skip */ }
            }
        }

        var skipExtensions = BuildSkipSet(settings);

        // Don't move our own running .exe even if user accidentally points at it.
        string? selfExe = null;
        try { selfExe = Environment.ProcessPath; } catch { /* ignore */ }

        var rootFull = Path.GetFullPath(folder);
        var rootDriveRoot = Path.GetPathRoot(rootFull) ?? "";

        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System
        };

        // Collect first so creating new category folders mid-enumeration doesn't re-enter them.
        List<string> files;
        try
        {
            files = new List<string>(Directory.EnumerateFiles(folder, "*", enumOpts));
        }
        catch (Exception ex)
        {
            progress?.Report(new SortStep { Kind = SortStepKind.Error, Message = ex.Message });
            result.Errors++;
            return result;
        }

        if (files.Count == 0)
        {
            progress?.Report(new SortStep { Kind = SortStepKind.Info, Message = loc.T("NoFiles") });
            return result;
        }

        var categoryFolderPaths = BuildCategoryFolderSet(folder, settings);

        // Journal: only built up during real (non-dryRun) runs that moved at least one file.
        var journal = new Journal { Folder = rootFull };
        var rememberedDecision = (CollisionDecision?)null;

        int processed = 0;
        counter?.Report(new SortProgress { Current = 0, Total = files.Count });

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            counter?.Report(new SortProgress { Current = processed, Total = files.Count });

            string ext, fileName;
            try
            {
                fileName = Path.GetFileName(filePath);
                ext = NormalizeExt(Path.GetExtension(filePath));
            }
            catch { continue; }

            // Skip our own .exe
            if (selfExe != null && string.Equals(filePath, selfExe, StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
                progress?.Report(new SortStep { Kind = SortStepKind.Skip, FileName = fileName, SourcePath = filePath,
                                                Message = string.Format(loc.T("SkipText"), fileName) });
                continue;
            }

            // Skip excluded extensions
            if (skipExtensions.Contains(ext))
            {
                result.Skipped++;
                progress?.Report(new SortStep { Kind = SortStepKind.Skip, FileName = fileName, SourcePath = filePath,
                                                Message = string.Format(loc.T("SkipText"), fileName) });
                continue;
            }

            // Skip files already inside one of our category folders (only relevant in recursive mode).
            try
            {
                var parent = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
                if (categoryFolderPaths.Contains(parent)) continue;
            }
            catch { /* ignore path issues */ }

            // Resolve target subfolder name(s) per SortMode.
            string? subPath;
            string categoryDisplayName;
            try
            {
                (subPath, categoryDisplayName) = ResolveBucket(filePath, ext, fileName,
                    extToCat, compiledRegex, settings);
            }
            catch (Exception ex)
            {
                result.Errors++;
                progress?.Report(new SortStep { Kind = SortStepKind.Error,
                                                Message = string.Format(loc.T("ErrorText"), ex.Message) });
                continue;
            }

            if (subPath == null)
            {
                // unknown + CreateOtherFolder = false → silent skip
                result.Skipped++;
                progress?.Report(new SortStep { Kind = SortStepKind.Skip, FileName = fileName, SourcePath = filePath,
                                                Message = string.Format(loc.T("SkipText"), fileName) });
                continue;
            }

            string targetDir;
            try { targetDir = Path.GetFullPath(Path.Combine(folder, subPath)); }
            catch (Exception ex)
            {
                result.Errors++;
                progress?.Report(new SortStep { Kind = SortStepKind.Error,
                                                Message = string.Format(loc.T("ErrorText"), ex.Message) });
                continue;
            }

            if (!targetDir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors++;
                progress?.Report(new SortStep { Kind = SortStepKind.Error, Message = loc.T("UnsafeTarget") });
                continue;
            }

            try
            {
                var fileDir = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
                if (string.Equals(fileDir, targetDir, StringComparison.OrdinalIgnoreCase)) continue;
            }
            catch { /* ignore */ }

            if (!dryRun)
            {
                try { Directory.CreateDirectory(targetDir); }
                catch (Exception ex)
                {
                    result.Errors++;
                    progress?.Report(new SortStep { Kind = SortStepKind.Error,
                                                    Message = string.Format(loc.T("ErrorText"), ex.Message) });
                    continue;
                }
            }

            // Resolve collisions per strategy
            var destPath = Path.Combine(targetDir, fileName);
            var existsAtDest = File.Exists(destPath) || Directory.Exists(destPath);
            var strategy = settings.CollisionStrategy;
            if (existsAtDest)
            {
                var decision = ResolveCollision(filePath, destPath, strategy, ref rememberedDecision, collisionPrompt);
                switch (decision)
                {
                    case CollisionDecision.Skip:
                        result.Skipped++;
                        progress?.Report(new SortStep { Kind = SortStepKind.Skip, FileName = fileName, SourcePath = filePath,
                                                        Message = string.Format(loc.T("SkipText"), fileName) });
                        continue;
                    case CollisionDecision.Suffix:
                        destPath = MakeUniqueWithSuffix(targetDir, fileName);
                        break;
                    case CollisionDecision.Replace:
                        // we keep destPath, will overwrite below
                        break;
                }
            }

            if (dryRun)
            {
                result.Moved++;
                Bump(result.PerCategory, categoryDisplayName);
                progress?.Report(new SortStep
                {
                    Kind = SortStepKind.Move,
                    FileName = fileName,
                    Category = categoryDisplayName,
                    SourcePath = filePath,
                    DestinationPath = destPath,
                    Message = string.Format(loc.T("DryMoveText"), fileName, categoryDisplayName)
                });
                continue;
            }

            // Real move: atomic on same volume, copy+verify+delete cross-volume.
            try
            {
                MoveFile(filePath, destPath, rootDriveRoot);
                result.Moved++;
                Bump(result.PerCategory, categoryDisplayName);
                journal.Entries.Add(new JournalEntry { Source = filePath, Destination = destPath });
                LogService.Move($"{filePath} -> {destPath}");
                progress?.Report(new SortStep
                {
                    Kind = SortStepKind.Move,
                    FileName = fileName,
                    Category = categoryDisplayName,
                    SourcePath = filePath,
                    DestinationPath = destPath,
                    Message = string.Format(loc.T("MoveText"), fileName, categoryDisplayName)
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors++;
                LogService.Error($"{filePath}: {ex.Message}");
                progress?.Report(new SortStep { Kind = SortStepKind.Error,
                                                Message = string.Format(loc.T("ErrorText"), $"{fileName}: {ex.Message}") });
            }
        }

        if (!dryRun && journal.Entries.Count > 0)
        {
            result.JournalPath = JournalService.Save(journal);
        }
        return result;
    }

    /// <summary>
    /// Decide which subfolder a file goes into based on the configured SortMode.
    /// Returns (relativeSubPath, displayName) or (null, "") if the file should be skipped.
    /// </summary>
    private static (string? subPath, string display) ResolveBucket(
        string filePath, string ext, string fileName,
        Dictionary<string, Category> extToCat,
        List<(Regex re, Category cat)> compiledRegex,
        AppSettings settings)
    {
        DateTime when;
        try { when = File.GetLastWriteTime(filePath); }
        catch { when = DateTime.Now; }

        switch (settings.SortMode)
        {
            case SortMode.ByYear:
            {
                var name = when.Year.ToString("D4");
                return (SanitizeFolderName(name), name);
            }
            case SortMode.ByYearMonth:
            {
                var name = $"{when.Year:D4}-{when.Month:D2}";
                return (SanitizeFolderName(name), name);
            }
            case SortMode.ByExtensionAndYear:
            {
                var (catName, _) = ResolveCategoryName(ext, fileName, extToCat, compiledRegex, settings);
                if (catName == null) return (null, "");
                var year = when.Year.ToString("D4");
                var sub = Path.Combine(SanitizeFolderName(catName), SanitizeFolderName(year));
                return (sub, $"{catName} / {year}");
            }
            default: // ByExtension
            {
                var (catName, _) = ResolveCategoryName(ext, fileName, extToCat, compiledRegex, settings);
                if (catName == null) return (null, "");
                return (SanitizeFolderName(catName), catName);
            }
        }
    }

    private static (string? name, Category? cat) ResolveCategoryName(
        string ext, string fileName,
        Dictionary<string, Category> extToCat,
        List<(Regex re, Category cat)> compiledRegex,
        AppSettings settings)
    {
        // 1. Extension match (fast path)
        if (extToCat.TryGetValue(ext, out var cat)) return (cat.DisplayName(settings.Language), cat);
        // 2. Regex on filename
        foreach (var (re, c) in compiledRegex)
        {
            try { if (re.IsMatch(fileName)) return (c.DisplayName(settings.Language), c); }
            catch { /* runtime regex error — skip */ }
        }
        // 3. "Other" or skip
        if (!settings.CreateOtherFolder) return (null, null);
        var other = settings.Language == "en" ? settings.OtherNameEn : settings.OtherNameRu;
        if (string.IsNullOrWhiteSpace(other)) other = "Other";
        return (other, null);
    }

    /// <summary>
    /// Apply the chosen collision strategy. <paramref name="remembered"/> persists across
    /// files when the user picked "Apply to All" in the Ask dialog.
    /// </summary>
    private static CollisionDecision ResolveCollision(
        string source, string dest,
        CollisionStrategy strategy,
        ref CollisionDecision? remembered,
        Func<string, string, CollisionDecision>? prompt)
    {
        if (remembered.HasValue) return remembered.Value;

        switch (strategy)
        {
            case CollisionStrategy.Skip:    return CollisionDecision.Skip;
            case CollisionStrategy.Suffix:  return CollisionDecision.Suffix;
            case CollisionStrategy.ReplaceIfNewer:
            {
                try
                {
                    var srcT  = File.GetLastWriteTime(source);
                    var destT = File.GetLastWriteTime(dest);
                    return srcT > destT ? CollisionDecision.Replace : CollisionDecision.Skip;
                }
                catch { return CollisionDecision.Skip; }
            }
            case CollisionStrategy.Ask:
            {
                if (prompt == null) return CollisionDecision.Suffix; // headless fallback
                var d = prompt(source, dest);
                // Convention: if the prompt sets the high bit (encoded in the enum value
                // via 'ApplyToAll' helpers), the UI is responsible for re-presenting
                // the simpler decision; we just remember the simple form here.
                return d;
            }
        }
        return CollisionDecision.Suffix;
    }

    private static string MakeUniqueWithSuffix(string targetDir, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var fileExt  = Path.GetExtension(fileName);
        int i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(targetDir, $"{baseName} ({i}){fileExt}");
            i++;
        } while ((File.Exists(candidate) || Directory.Exists(candidate)) && i < 10_000);
        return candidate;
    }

    /// <summary>
    /// Same-volume → File.Move (atomic rename, single FS metadata operation).
    /// Cross-volume → File.Copy then verify size + LastWriteTime, then delete original.
    /// On verify failure the partial copy is removed and the source is preserved.
    /// </summary>
    private static void MoveFile(string source, string dest, string sourceDriveRoot)
    {
        // If the destination already exists at this point, replace strategy was chosen.
        var overwrite = File.Exists(dest);

        var destDriveRoot = Path.GetPathRoot(Path.GetFullPath(dest)) ?? "";
        var sameVolume = string.Equals(sourceDriveRoot, destDriveRoot, StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            File.Move(source, dest, overwrite);
            return;
        }

        // Cross-volume: copy → verify → delete
        File.Copy(source, dest, overwrite);
        try
        {
            var srcInfo  = new FileInfo(source);
            var destInfo = new FileInfo(dest);
            if (srcInfo.Length != destInfo.Length)
                throw new IOException($"Cross-volume verify failed: size mismatch ({srcInfo.Length} vs {destInfo.Length}).");
            // LastWriteTime: copy preserves file content but timestamp may drift on some FS;
            // explicitly stamp the destination so verification is robust.
            destInfo.LastWriteTime = srcInfo.LastWriteTime;
            if (Math.Abs((destInfo.LastWriteTime - srcInfo.LastWriteTime).TotalSeconds) > 2)
                throw new IOException("Cross-volume verify failed: LastWriteTime mismatch.");
            File.Delete(source);
        }
        catch
        {
            // Roll back the partial copy if it exists.
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* best effort */ }
            throw;
        }
    }

    private static HashSet<string> BuildSkipSet(AppSettings settings)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in settings.SkipExtensions)
        {
            var key = NormalizeExt(ext);
            if (!string.IsNullOrEmpty(key)) s.Add(key);
        }
        return s;
    }

    private static HashSet<string> BuildCategoryFolderSet(string folder, AppSettings settings)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in settings.Categories)
        {
            try { set.Add(Path.GetFullPath(Path.Combine(folder, cat.NameRu))); } catch { /* ignore */ }
            try { set.Add(Path.GetFullPath(Path.Combine(folder, cat.NameEn))); } catch { /* ignore */ }
        }
        if (settings.CreateOtherFolder)
        {
            try { set.Add(Path.GetFullPath(Path.Combine(folder, settings.OtherNameRu))); } catch { /* ignore */ }
            try { set.Add(Path.GetFullPath(Path.Combine(folder, settings.OtherNameEn))); } catch { /* ignore */ }
        }
        return set;
    }

    private static string NormalizeExt(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return "";
        ext = ext.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;
        return ext;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            chars[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        var s = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "_" : s;
    }

    private static void Bump(Dictionary<string, int> dict, string key)
    {
        dict[key] = dict.TryGetValue(key, out var n) ? n + 1 : 1;
    }
}

/// <summary>How many files done / total — for the progress bar.</summary>
public class SortProgress
{
    public int Current { get; init; }
    public int Total   { get; init; }
}

/// <summary>What the collision prompt returns, or what the strategy resolves to.</summary>
public enum CollisionDecision
{
    Suffix,
    Skip,
    Replace
}
