using System;
using System.IO;
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

        // Reverse order: undo in opposite order of moves so name suffixes resolve sanely.
        for (int i = journal.Entries.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var e = journal.Entries[i];
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

        // Consume the journal so a second click of "Undo last" doesn't double-undo.
        JournalService.Delete(journalPath);
        return result;
    }
}
