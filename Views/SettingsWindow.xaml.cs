using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.Views;

public partial class SettingsWindow : Window
{
    public ObservableCollection<CategoryRow> CategoryRows { get; } = new();
    public ObservableCollection<string> ExtensionRows { get; } = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += SettingsWindow_Loaded;
        SourceInitialized += (_, _) => ThemeService.Current.ApplyChromeTo(this);
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Cached;

        CategoryRows.Clear();
        foreach (var c in s.Categories)
            CategoryRows.Add(new CategoryRow
            {
                Id     = c.Id,
                NameRu = c.NameRu,
                NameEn = c.NameEn,
                ExtensionsCsv = string.Join(", ", c.Extensions),
                NameRegexCsv  = string.Join("; ", c.NameRegex)
            });
        CategoryGrid.ItemsSource = CategoryRows;

        ExtensionRows.Clear();
        foreach (var ext in s.SkipExtensions) ExtensionRows.Add(ext);
        ExtList.ItemsSource = ExtensionRows;

        DefaultRecursiveCheck.IsChecked = s.Recursive;
        UseOtherFolderCheck.IsChecked   = s.CreateOtherFolder;
        OtherNameRuBox.Text = s.OtherNameRu;
        OtherNameEnBox.Text = s.OtherNameEn;

        // Collision strategy combo
        CollisionCombo.SelectedIndex = s.CollisionStrategy switch
        {
            CollisionStrategy.Skip            => 1,
            CollisionStrategy.ReplaceIfNewer  => 2,
            CollisionStrategy.Ask             => 3,
            _                                 => 0
        };

        // Sort mode radios
        switch (s.SortMode)
        {
            case SortMode.ByYear:              ModeYearRadio.IsChecked       = true; break;
            case SortMode.ByYearMonth:         ModeYearMonthRadio.IsChecked  = true; break;
            case SortMode.ByExtensionAndYear:  ModeExtAndYearRadio.IsChecked = true; break;
            default:                           ModeExtensionRadio.IsChecked  = true; break;
        }
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var row = new CategoryRow
        {
            Id     = "new_" + (CategoryRows.Count + 1),
            NameRu = "Новая",
            NameEn = "New",
            ExtensionsCsv = "",
            NameRegexCsv = ""
        };
        CategoryRows.Add(row);
        CategoryGrid.SelectedItem = row;
        CategoryGrid.ScrollIntoView(row);
    }

    private void RemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryGrid.SelectedItem is CategoryRow row)
            CategoryRows.Remove(row);
    }

    private void AddExt_Click(object sender, RoutedEventArgs e)
    {
        var raw = NewExtBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) return;
        if (!raw.StartsWith('.')) raw = "." + raw;
        raw = raw.ToLowerInvariant();
        if (!ExtensionRows.Contains(raw, StringComparer.OrdinalIgnoreCase))
            ExtensionRows.Add(raw);
        NewExtBox.Text = "";
    }

    private void RemoveExt_Click(object sender, RoutedEventArgs e)
    {
        if (ExtList.SelectedItem is string s) ExtensionRows.Remove(s);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Commit any in-progress DataGrid edits before reading values.
        CategoryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CategoryGrid.CommitEdit(DataGridEditingUnit.Row, true);

        ValidationText.Text = "";
        if (!ValidateRows(out var error))
        {
            ValidationText.Text = error;
            // Don't close — give the user a chance to fix it.
            return;
        }

        var s = SettingsService.Cached;
        s.Categories = CategoryRows.Select(r => new Category
        {
            Id     = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id.Trim(),
            NameRu = r.NameRu?.Trim() ?? "",
            NameEn = r.NameEn?.Trim() ?? "",
            Extensions = ParseExtensions(r.ExtensionsCsv ?? ""),
            NameRegex  = ParseRegex(r.NameRegexCsv ?? "")
        }).ToList();

        s.SkipExtensions = ExtensionRows.Select(NormalizeExt)
                                        .Where(e => !string.IsNullOrEmpty(e))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();

        s.Recursive          = DefaultRecursiveCheck.IsChecked == true;
        s.CreateOtherFolder  = UseOtherFolderCheck.IsChecked == true;
        s.OtherNameRu        = string.IsNullOrWhiteSpace(OtherNameRuBox.Text) ? "Прочее" : OtherNameRuBox.Text.Trim();
        s.OtherNameEn        = string.IsNullOrWhiteSpace(OtherNameEnBox.Text) ? "Other"  : OtherNameEnBox.Text.Trim();

        // Collision strategy
        s.CollisionStrategy = (CollisionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Skip"           => CollisionStrategy.Skip,
            "ReplaceIfNewer" => CollisionStrategy.ReplaceIfNewer,
            "Ask"            => CollisionStrategy.Ask,
            _                => CollisionStrategy.Suffix
        };

        // Sort mode
        if (ModeYearRadio.IsChecked == true)            s.SortMode = SortMode.ByYear;
        else if (ModeYearMonthRadio.IsChecked == true)  s.SortMode = SortMode.ByYearMonth;
        else if (ModeExtAndYearRadio.IsChecked == true) s.SortMode = SortMode.ByExtensionAndYear;
        else                                            s.SortMode = SortMode.ByExtension;

        try
        {
            SettingsService.Save(s);
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Validate every row: ID required, at least one name, no duplicate IDs, regex compilable.
    /// Returns true on success, otherwise sets <paramref name="error"/> with a localized message.
    /// </summary>
    private bool ValidateRows(out string error)
    {
        error = "";
        var loc = LocalizationService.Current;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in CategoryRows)
        {
            if (string.IsNullOrWhiteSpace(r.Id))
            {
                error = loc.T("ValidationEmptyId");
                CategoryGrid.SelectedItem = r;
                CategoryGrid.ScrollIntoView(r);
                return false;
            }
            if (string.IsNullOrWhiteSpace(r.NameRu) && string.IsNullOrWhiteSpace(r.NameEn))
            {
                error = loc.T("ValidationEmptyName");
                CategoryGrid.SelectedItem = r;
                CategoryGrid.ScrollIntoView(r);
                return false;
            }
            var id = r.Id.Trim();
            if (!seenIds.Add(id))
            {
                error = string.Format(loc.T("ValidationDuplicateId"), id);
                CategoryGrid.SelectedItem = r;
                CategoryGrid.ScrollIntoView(r);
                return false;
            }
            foreach (var pat in ParseRegex(r.NameRegexCsv ?? ""))
            {
                try { _ = new Regex(pat); }
                catch (ArgumentException ex)
                {
                    error = string.Format(loc.T("ValidationBadRegex"),
                                          string.IsNullOrWhiteSpace(r.NameRu) ? r.NameEn : r.NameRu,
                                          ex.Message);
                    CategoryGrid.SelectedItem = r;
                    CategoryGrid.ScrollIntoView(r);
                    return false;
                }
            }
        }
        return true;
    }

    private static List<string> ParseExtensions(string csv)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in csv.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' },
                                       StringSplitOptions.RemoveEmptyEntries))
        {
            var n = NormalizeExt(part);
            if (!string.IsNullOrEmpty(n) && seen.Add(n)) list.Add(n);
        }
        return list;
    }

    private static List<string> ParseRegex(string csv)
    {
        var list = new List<string>();
        // Regex separator is ';' only — commas and spaces are valid inside regex.
        foreach (var part in csv.Split(new[] { ';', '\n', '\r' },
                                       StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    private static string NormalizeExt(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim().ToLowerInvariant();
        if (!s.StartsWith('.')) s = "." + s;
        return s;
    }
}

/// <summary>DataGrid row — flat strings so DataGrid can edit extensions/regex as CSV strings.</summary>
public class CategoryRow : INotifyPropertyChanged
{
    private string _id = "", _ru = "", _en = "", _csv = "", _rx = "";

    public string Id            { get => _id;  set { _id  = value; OnChanged(nameof(Id)); } }
    public string NameRu        { get => _ru;  set { _ru  = value; OnChanged(nameof(NameRu)); } }
    public string NameEn        { get => _en;  set { _en  = value; OnChanged(nameof(NameEn)); } }
    public string ExtensionsCsv { get => _csv; set { _csv = value; OnChanged(nameof(ExtensionsCsv)); } }
    public string NameRegexCsv  { get => _rx;  set { _rx  = value; OnChanged(nameof(NameRegexCsv)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
