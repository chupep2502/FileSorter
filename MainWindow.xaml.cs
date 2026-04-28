using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FileSorter.Models;
using FileSorter.Services;
using FileSorter.Views;
using Microsoft.Win32;

namespace FileSorter;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _suppressLanguageEvent;
    private bool _suppressThemeEvent;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => ThemeService.Current.ApplyChromeTo(this);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Cached;

        // Initialize folder combo with recent folders + a sensible default.
        FolderCombo.ItemsSource = settings.RecentFolders;
        var initial = settings.RecentFolders.Count > 0
            ? settings.RecentFolders[0]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        FolderCombo.Text = initial;

        // Initial checkbox state from defaults.
        RecursiveCheck.IsChecked = settings.Recursive;

        // Sync language combo with current language without firing the event.
        _suppressLanguageEvent = true;
        LanguageCombo.SelectedIndex = settings.Language == "en" ? 1 : 0;
        _suppressLanguageEvent = false;

        // Sync theme combo with current theme.
        _suppressThemeEvent = true;
        ThemeCombo.SelectedIndex = settings.Theme switch
        {
            "light" => 1,
            "dark"  => 2,
            _       => 0,
        };
        _suppressThemeEvent = false;

        // Bottom-right version label.
        VersionText.Text = string.Format(
            LocalizationService.Current.T("VersionLabel"), VersionInfo.Display);

        // Enable Undo button if a journal from a previous session is still around.
        UndoButton.IsEnabled = JournalService.GetLatestPath() != null;

        // Live filter on the log.
        var view = CollectionViewSource.GetDefaultView(LogList.Items);
        view.Filter = LogFilterPredicate;
    }

    // ----- log filter -----

    private void LogFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(LogList.Items).Refresh();
    }

    private bool LogFilterPredicate(object obj)
    {
        var needle = LogFilterBox?.Text ?? "";
        if (string.IsNullOrEmpty(needle)) return true;
        var item = obj as ListBoxItem;
        var content = item?.Content?.ToString() ?? "";
        return content.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ----- top-bar combos -----

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeEvent) return;
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Tag?.ToString() ?? "system";
        ThemeService.Current.Apply(mode);

        var settings = SettingsService.Cached;
        settings.Theme = mode;
        try { SettingsService.Save(settings); } catch { /* ignore */ }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvent) return;
        if (LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        var lang = item.Tag?.ToString() ?? "ru";
        LocalizationService.Current.SetLanguage(lang);

        var settings = SettingsService.Cached;
        settings.Language = lang;
        try { SettingsService.Save(settings); } catch { /* ignore */ }

        VersionText.Text = string.Format(
            LocalizationService.Current.T("VersionLabel"), VersionInfo.Display);
    }

    // ----- folder picker -----

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = LocalizationService.Current.T("BrowseButton"),
            Multiselect = false
        };

        var current = FolderCombo.Text;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dlg.InitialDirectory = current;

        if (dlg.ShowDialog(this) == true)
        {
            FolderCombo.Text = dlg.FolderName;
        }
    }

    // ----- drag & drop -----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryExtractFolder(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryExtractFolder(e.Data, out var folder))
        {
            FolderCombo.Text = folder;
            e.Handled = true;
        }
    }

    private static bool TryExtractFolder(IDataObject data, out string folder)
    {
        folder = "";
        if (data == null) return false;
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return false;
        var p = paths[0];
        if (Directory.Exists(p)) { folder = p; return true; }
        // If a file was dropped, use its parent directory.
        if (File.Exists(p))
        {
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) { folder = dir; return true; }
        }
        return false;
    }

    // ----- top-level buttons -----

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e) => RunSort(dryRun: true);

    private void SortButton_Click(object sender, RoutedEventArgs e) => RunSort(dryRun: false);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Current;
        var path = JournalService.GetLatestPath();
        if (path == null)
        {
            AppendLog(loc.T("NoUndoAvailable"), "LogErrorBrush");
            UndoButton.IsEnabled = false;
            return;
        }

        SetBusy(true);
        StatusText.Text = "...";
        var progress = new Progress<SortStep>(step =>
        {
            var key = step.Kind switch
            {
                SortStepKind.Move  => "LogMoveBrush",
                SortStepKind.Skip  => "LogSkipBrush",
                SortStepKind.Error => "LogErrorBrush",
                _                  => "LogInfoBrush"
            };
            AppendLog(step.Message, key, srcPath: step.SourcePath, destPath: step.DestinationPath);
        });

        _cts = new CancellationTokenSource();
        try
        {
            var r = await UndoService.UndoAsync(path, progress, _cts.Token);
            var summary = string.Format(loc.T("UndoSummary"), r.Reverted, r.Skipped, r.Errors);
            StatusText.Text = summary;
            AppendLog("", "LogInfoBrush");
            AppendLog(summary, "LogInfoBrush", bold: true);
        }
        catch (OperationCanceledException)
        {
            AppendLog("— canceled —", "LogErrorBrush");
            StatusText.Text = loc.T("Ready");
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(loc.T("ErrorText"), ex.Message), "LogErrorBrush");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            UndoButton.IsEnabled = JournalService.GetLatestPath() != null;
        }
    }

    // ----- sort core -----

    private async void RunSort(bool dryRun)
    {
        var loc = LocalizationService.Current;
        var folder = FolderCombo.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(folder))
        {
            AppendLog(loc.T("ChooseFolderFirst"), "LogErrorBrush");
            return;
        }

        // System-folder protection.
        var validation = SorterService.ValidateRoot(folder);
        switch (validation)
        {
            case RootValidation.NotFound:
                AppendLog(string.Format(loc.T("FolderNotFound"), folder), "LogErrorBrush");
                return;
            case RootValidation.Empty:
                AppendLog(loc.T("ChooseFolderFirst"), "LogErrorBrush");
                return;
            case RootValidation.SystemFolder:
                MessageBox.Show(this, loc.T("RefuseSystemFolder"), loc.T("AppTitle"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                AppendLog(loc.T("RefuseSystemFolder"), "LogErrorBrush");
                return;
            case RootValidation.DriveRoot:
                MessageBox.Show(this, loc.T("RefuseDriveRoot"), loc.T("AppTitle"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                AppendLog(loc.T("RefuseDriveRoot"), "LogErrorBrush");
                return;
        }

        LogList.Items.Clear();
        var settings = SettingsService.Cached;

        // Pre-count to enable confirmation prompt + accurate progress.
        var (count, totalBytes) = SorterService.CountFiles(folder, settings,
            recursive: RecursiveCheck.IsChecked == true);
        if (!dryRun && (count > 500 || totalBytes > 1L * 1024 * 1024 * 1024))
        {
            var human = HumanBytes(totalBytes);
            var msg   = string.Format(loc.T("LargeSortConfirm"), count, human);
            var ans   = MessageBox.Show(this, msg, loc.T("AppTitle"),
                                        MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
        }

        settings.TouchRecent(folder);
        settings.Recursive = RecursiveCheck.IsChecked == true;
        try { SettingsService.Save(settings); } catch { /* not fatal */ }
        FolderCombo.ItemsSource = null;
        FolderCombo.ItemsSource = settings.RecentFolders;
        FolderCombo.Text = folder;

        SetBusy(true, totalKnown: count > 0);
        StatusText.Text = "...";

        var progress = new Progress<SortStep>(step =>
        {
            var key = step.Kind switch
            {
                SortStepKind.Move  => "LogMoveBrush",
                SortStepKind.Skip  => "LogSkipBrush",
                SortStepKind.Error => "LogErrorBrush",
                _                  => "LogInfoBrush"
            };
            AppendLog(step.Message, key, srcPath: step.SourcePath, destPath: step.DestinationPath);
        });

        var counter = new Progress<SortProgress>(p =>
        {
            ProgressBar.Maximum = p.Total > 0 ? p.Total : 1;
            ProgressBar.Value   = p.Current;
            ProgressText.Text   = string.Format(loc.T("ProgressFormat"), p.Current, p.Total);
        });

        // Collision prompt — runs on UI thread via dispatcher.
        // If the user ticks "Apply to all", we remember the choice here in the closure
        // and short-circuit subsequent calls without showing the dialog again.
        CollisionDecision? remembered = null;
        Func<string, string, CollisionDecision> prompt = (src, dst) =>
        {
            if (remembered.HasValue) return remembered.Value;
            return Dispatcher.Invoke(() =>
            {
                var dlg = new CollisionDialog(src) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    if (dlg.ApplyToAll) remembered = dlg.Decision;
                    return dlg.Decision;
                }
                return CollisionDecision.Skip;
            });
        };

        _cts = new CancellationTokenSource();
        try
        {
            var result = await SorterService.SortAsync(folder, settings,
                recursive: RecursiveCheck.IsChecked == true,
                dryRun: dryRun,
                progress: progress,
                counter: counter,
                collisionPrompt: prompt,
                ct: _cts.Token);

            var summary = dryRun
                ? string.Format(loc.T("DryDoneSummary"), result.Moved, result.Skipped)
                : string.Format(loc.T("DoneSummary"), result.Moved, result.Skipped, result.Errors);
            StatusText.Text = summary;
            AppendLog("", "LogInfoBrush");
            AppendLog(summary, "LogInfoBrush", bold: true);
            foreach (var kv in result.PerCategory)
                AppendLog($"  {kv.Key}: {kv.Value}", "LogSkipBrush");

            UndoButton.IsEnabled = !dryRun && result.JournalPath != null;
        }
        catch (OperationCanceledException)
        {
            AppendLog("— canceled —", "LogErrorBrush");
            StatusText.Text = loc.T("Ready");
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(loc.T("ErrorText"), ex.Message), "LogErrorBrush");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ----- log helpers -----

    private void AppendLog(string text, string brushResourceKey, bool bold = false,
                           string? srcPath = null, string? destPath = null)
    {
        var item = new ListBoxItem
        {
            Content = text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            // Tag carries the path used by double-click handler.
            Tag = destPath ?? srcPath
        };
        item.SetResourceReference(ListBoxItem.ForegroundProperty, brushResourceKey);
        LogList.Items.Add(item);
        LogList.ScrollIntoView(item);
    }

    private void LogList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is not ListBoxItem item) return;
        if (item.Tag is not string path || string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                // Fallback: open the parent directory.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch { /* ignore — explorer failures are rare and not actionable */ }
    }

    // ----- busy state -----

    private void SetBusy(bool busy, bool totalKnown = false)
    {
        PreviewButton.IsEnabled  = !busy;
        SortButton.IsEnabled     = !busy;
        BrowseButton.IsEnabled   = !busy;
        FolderCombo.IsEnabled    = !busy;
        RecursiveCheck.IsEnabled = !busy;
        LanguageCombo.IsEnabled  = !busy;
        UndoButton.IsEnabled     = !busy && JournalService.GetLatestPath() != null;
        CancelButton.IsEnabled   = busy;
        ProgressRow.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            ProgressBar.Value = 0;
            ProgressText.Text = "";
        }
    }

    // ----- formatting -----

    private static string HumanBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < suffixes.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {suffixes[i]}";
    }
}
