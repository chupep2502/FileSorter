using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileSorter.Models;

namespace FileSorter.Services;

public class UndoResult
{
    public int Reverted { get; set; }
    public int Skipped  { get; set; }
    public int Errors   { get; set; }
}

/// <summary>Reverses moves recorded by <see cref="JournalService"/>.</summary>
public static class UndoService
{
    public static Task<UndoResult> UndoAsync(
        string journalPath,
        IProgress<SortStep>? progress,
        CancellationToken ct)
    {
        return Task.Run(() => UndoInternal(journalPath, progress, ct), ct);
    }

    private static UndoResult UndoInternal(string journalPath, IProgress<SortStep>? progress, CancellationToken ct)
    {
        var loc = LocalizationService.Current;
        var result = new UndoResult();
        var journal = JournalService.Load(journalPath);
        if (journal == null)
        {
            progress?.Report(new SortStep
            {
                Kind = SortStepKind.Error,
                Message = string.Format(loc.T("ErrorText"), "journal load failed")
            });
            result.Errors++;
            return result;
        }

        // Track destination directories so we can prune the empty ones at the end.
        // Case-insensitive — matches Windows filesystem semantics.
        var destDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The "root" the user originally sorted (parent of all source files). We never
        // delete this — only category subfolders inside it.
        var sortRoot = "";
        if (journal.Entries.Count > 0)
        {
            sortRoot = Path.GetDirectoryName(journal.Entries[0].Source) ?? "";
        }

        // Reverse order: undo in opposite order of moves so name suffixes resolve sanely.
        for (int i = journal.Entries.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var e = journal.Entries[i];

            var destDir = Path.GetDirectoryName(e.Destination);
            if (!string.IsNullOrEmpty(destDir)) destDirs.Add(destDir);

            try
            {
                if (!File.Exists(e.Destination))
                {
                    result.Skipped++;
                    progress?.Report(new SortStep
                    {
                        Kind = SortStepKind.Skip,
                        Message = string.Format(loc.T("SkipText"), Path.GetFileName(e.Destination))
                    });
                    continue;
                }

                if (File.Exists(e.Source))
                {
                    // Source has been re-created in the meantime — don't overwrite.
                    result.Skipped++;
                    progress?.Report(new SortStep
                    {
                        Kind = SortStepKind.Skip,
                        Message = string.Format(loc.T("SkipText"), Path.GetFileName(e.Destination))
                    });
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(e.Source) ?? "");
                File.Move(e.Destination, e.Source);
                result.Reverted++;
                LogService.Move($"UNDO {e.Destination} -> {e.Source}");
                progress?.Report(new SortStep
                {
                    Kind = SortStepKind.Move,
                    SourcePath = e.Destination,
                    DestinationPath = e.Source,
                    Message = string.Format(loc.T("MoveText"),
                                            Path.GetFileName(e.Destination),
                                            Path.GetFileName(e.Source))
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors++;
                LogService.Error("Undo failed: " + ex.Message);
                progress?.Report(new SortStep
                {
                    Kind = SortStepKind.Error,
                    Message = string.Format(loc.T("ErrorText"), ex.Message)
                });
            }
        }

        // Prune empty category directories.
        // Sort by depth descending so child folders (e.g. "Документы\2024") are checked
        // before their parents ("Документы"). Walk up to — but not into — sortRoot.
        TryPruneEmptyDirs(destDirs, sortRoot);

        // Consume the journal so a second click of "Undo last" doesn't double-undo.
        JournalService.Delete(journalPath);
        return result;
    }

    /// <summary>
    /// Delete every directory in <paramref name="dirs"/> that is empty after undo,
    /// then walk up its parent chain doing the same — but never touch
    /// <paramref name="sortRoot"/> itself or anything outside it.
    /// </summary>
    private static void TryPruneEmptyDirs(HashSet<string> dirs, string sortRoot)
    {
        if (string.IsNullOrEmpty(sortRoot)) return;
        var rootFull = Path.GetFullPath(sortRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Process deepest first.
        var ordered = dirs.OrderByDescending(d => d.Count(c =>
            c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var initial in ordered)
        {
            var current = initial;
            while (!string.IsNullOrEmpty(current))
            {
                var fullCurrent = Path.GetFullPath(current).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!visited.Add(fullCurrent)) break;

                // Stop when we reach (or pass) the sort root.
                if (string.Equals(fullCurrent, rootFull, StringComparison.OrdinalIgnoreCase)) break;
                if (!fullCurrent.StartsWith(rootFull + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) break;

                if (!Directory.Exists(fullCurrent)) break;

                // Only delete if truly empty (no files, no subdirs).
                var hasAny = Directory.EnumerateFileSystemEntries(fullCurrent).Any();
                if (hasAny) break;

                try
                {
                    Directory.Delete(fullCurrent, recursive: false);
                    LogService.Info($"UNDO removed empty dir: {fullCurrent}");
                }
                catch (Exception ex)
                {
                    LogService.Error($"UNDO could not remove {fullCurrent}: {ex.Message}");
                    break;
                }

                current = Path.GetDirectoryName(fullCurrent);
            }
        }
    }
}
