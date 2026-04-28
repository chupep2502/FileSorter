using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Tests;

public class SorterServiceTests : IDisposable
{
    private readonly string _root;

    public SorterServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FileSorter.Tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Touch(string name, DateTime? when = null)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, "x");
        if (when.HasValue) File.SetLastWriteTime(p, when.Value);
        return p;
    }

    private static AppSettings Defaults() => AppSettings.CreateDefault();

    [Fact]
    public void DryRun_DoesNotMoveFiles()
    {
        var path = Touch("a.pdf");
        var settings = Defaults();
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: true,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.True(File.Exists(path), "Dry run must not move files");
    }

    [Fact]
    public void Real_MovesPdfIntoDocuments()
    {
        Touch("a.pdf");
        var settings = Defaults();
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.False(File.Exists(Path.Combine(_root, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(_root, "Документы", "a.pdf")));
    }

    [Fact]
    public void SkipExtension_SkipsLnk()
    {
        Touch("shortcut.lnk");
        var settings = Defaults();
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, "shortcut.lnk")));
    }

    [Fact]
    public void Collision_Suffix_AppendsParentheses()
    {
        // Pre-create destination
        var docsDir = Path.Combine(_root, "Документы");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "a.pdf"), "old");
        Touch("a.pdf");

        var settings = Defaults();
        settings.CollisionStrategy = CollisionStrategy.Suffix;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.True(File.Exists(Path.Combine(docsDir, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(docsDir, "a (1).pdf")));
    }

    [Fact]
    public void Collision_Skip_LeavesSourceInPlace()
    {
        var docsDir = Path.Combine(_root, "Документы");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "a.pdf"), "old");
        Touch("a.pdf");

        var settings = Defaults();
        settings.CollisionStrategy = CollisionStrategy.Skip;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, "a.pdf")));
    }

    [Fact]
    public void Collision_ReplaceIfNewer_OverwritesOnlyIfSourceNewer()
    {
        var docsDir = Path.Combine(_root, "Документы");
        Directory.CreateDirectory(docsDir);
        var dest = Path.Combine(docsDir, "a.pdf");
        File.WriteAllText(dest, "old");
        File.SetLastWriteTime(dest, DateTime.Now.AddDays(-2));

        var src = Touch("a.pdf");
        File.WriteAllText(src, "new");
        File.SetLastWriteTime(src, DateTime.Now); // newer

        var settings = Defaults();
        settings.CollisionStrategy = CollisionStrategy.ReplaceIfNewer;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.Equal("new", File.ReadAllText(dest));
    }

    [Fact]
    public void OtherFolder_CatchesUnknownExtensions()
    {
        Touch("a.unknown");
        var settings = Defaults();
        settings.CreateOtherFolder = true;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.True(File.Exists(Path.Combine(_root, "Прочее", "a.unknown")));
    }

    [Fact]
    public void NoOtherFolder_SkipsUnknownExtensions()
    {
        Touch("a.unknown");
        var settings = Defaults();
        settings.CreateOtherFolder = false;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, "a.unknown")));
    }

    [Fact]
    public void NameRegex_MatchesUnknownExtensionToCategory()
    {
        Touch("IMG_12345.bin");
        var settings = Defaults();
        // .bin is not in any category by default; we add a regex to "images" instead.
        settings.Categories.First(c => c.Id == "images").NameRegex.Add("^IMG_\\d+");
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.True(File.Exists(Path.Combine(_root, "Изображения", "IMG_12345.bin")));
    }

    [Fact]
    public void SortByYear_BucketsByLastWriteTime()
    {
        Touch("a.pdf", new DateTime(2023, 6, 1));
        Touch("b.pdf", new DateTime(2024, 6, 1));
        var settings = Defaults();
        settings.SortMode = SortMode.ByYear;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(2, r.Moved);
        Assert.True(File.Exists(Path.Combine(_root, "2023", "a.pdf")));
        Assert.True(File.Exists(Path.Combine(_root, "2024", "b.pdf")));
    }

    [Fact]
    public void SortByYearMonth_FormatsZeroPadded()
    {
        Touch("a.pdf", new DateTime(2024, 3, 5));
        var settings = Defaults();
        settings.SortMode = SortMode.ByYearMonth;
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.True(File.Exists(Path.Combine(_root, "2024-03", "a.pdf")));
    }

    [Fact]
    public void Counter_ReportsTotalAndCurrent()
    {
        Touch("a.pdf");
        Touch("b.pdf");
        Touch("c.pdf");
        var totals = new List<(int cur, int tot)>();
        var counter = new Progress<SortProgress>(p => totals.Add((p.Current, p.Total)));
        var settings = Defaults();
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: true,
            progress: null, counter: counter, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(3, r.Moved);
        // last reported total should equal file count
        Assert.True(totals.Count > 0);
        Assert.Equal(3, totals[^1].tot);
        Assert.Equal(3, totals[^1].cur);
    }

    [Fact]
    public void ValidateRoot_RejectsDriveRoot()
    {
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        Assert.Equal(RootValidation.DriveRoot, SorterService.ValidateRoot(sysDrive));
    }

    [Fact]
    public void ValidateRoot_RejectsWindowsFolder()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(win))
            Assert.Equal(RootValidation.SystemFolder, SorterService.ValidateRoot(win));
    }

    [Fact]
    public void ValidateRoot_AcceptsNormalFolder()
    {
        Assert.Equal(RootValidation.Ok, SorterService.ValidateRoot(_root));
    }

    [Fact]
    public void ValidateRoot_RejectsMissingFolder()
    {
        Assert.Equal(RootValidation.NotFound, SorterService.ValidateRoot(Path.Combine(_root, "no-such-dir")));
    }

    [Fact]
    public void CountFiles_ExcludesSkipExtensions()
    {
        Touch("a.pdf");
        Touch("b.lnk");
        Touch("c.unknown");
        var settings = Defaults();
        var (count, _) = SorterService.CountFiles(_root, settings, recursive: false);
        // .lnk skipped, .pdf and .unknown counted
        Assert.Equal(2, count);
    }

    [Fact]
    public void Recursive_DoesNotResortAlreadySortedFiles()
    {
        var docs = Path.Combine(_root, "Документы");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "old.pdf"), "old");
        Touch("new.pdf");

        var settings = Defaults();
        var r = SorterService.SortAsync(_root, settings, recursive: true, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved); // only new.pdf, old.pdf left alone
        Assert.True(File.Exists(Path.Combine(docs, "old.pdf")));
        Assert.True(File.Exists(Path.Combine(docs, "new.pdf")));
    }

    [Fact]
    public void Real_WritesUndoJournal()
    {
        Touch("a.pdf");
        var settings = Defaults();
        var r = SorterService.SortAsync(_root, settings, recursive: false, dryRun: false,
            progress: null, counter: null, collisionPrompt: null, ct: CancellationToken.None).Result;
        Assert.Equal(1, r.Moved);
        Assert.NotNull(r.JournalPath);
        Assert.True(File.Exists(r.JournalPath));

        // Cleanup the journal we just created so the test doesn't pollute %APPDATA%.
        try { File.Delete(r.JournalPath!); } catch { /* best effort */ }
    }
}
