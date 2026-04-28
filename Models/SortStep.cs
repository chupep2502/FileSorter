namespace FileSorter.Models;

public enum SortStepKind { Move, Skip, Error, Info }

/// <summary>One progress event reported during a sort run.</summary>
public class SortStep
{
    public SortStepKind Kind { get; init; }
    public string Message { get; init; } = "";
    public string? FileName { get; init; }
    public string? Category { get; init; }

    /// <summary>Absolute source path (filled for Move/Skip steps so the UI can open it).</summary>
    public string? SourcePath { get; init; }

    /// <summary>Absolute destination path (filled for Move steps).</summary>
    public string? DestinationPath { get; init; }
}

public class SortResult
{
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public bool DryRun { get; set; }
    public System.Collections.Generic.Dictionary<string, int> PerCategory { get; } = new();

    /// <summary>
    /// Path of the journal file the sort wrote (in %APPDATA%\FileSorter\history\).
    /// null for dry-runs and zero-move runs. Used by the Undo button.
    /// </summary>
    public string? JournalPath { get; set; }
}
