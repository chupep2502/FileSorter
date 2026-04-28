using System.Collections.Generic;
using System.ComponentModel;

namespace FileSorter.Services;

/// <summary>
/// Singleton localization with INotifyPropertyChanged. UI binds via the indexer:
///   {Binding [BrowseButton], Source={x:Static svc:LocalizationService.Current}}
/// Switching language fires PropertyChanged("Item[]") so all bindings refresh at once.
/// </summary>
public class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Current { get; } = new();

    private string _language = "ru";
    public string Language
    {
        get => _language;
        private set
        {
            if (_language == value) return;
            _language = value;
            // Notify bindings to refresh (indexer + Language property).
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetLanguage(string lang)
    {
        Language = (lang == "en") ? "en" : "ru";
    }

    /// <summary>XAML-friendly indexer: returns the localized string for a key.</summary>
    public string this[string key]
    {
        get
        {
            if (_strings.TryGetValue(key, out var pair))
                return Language == "en" ? pair.en : pair.ru;
            return key;
        }
    }

    /// <summary>For code-behind, mirrors the indexer.</summary>
    public string T(string key) => this[key];

    private static readonly Dictionary<string, (string ru, string en)> _strings = new()
    {
        // Window titles
        ["AppTitle"]              = ("Сортировщик файлов", "File Sorter"),
        ["SettingsTitle"]         = ("Настройки", "Settings"),

        // Main window
        ["FolderLabel"]           = ("Папка:", "Folder:"),
        ["BrowseButton"]          = ("Обзор...", "Browse..."),
        ["RecursiveCheckbox"]     = ("Включая вложенные папки", "Include subfolders"),
        ["DryRunCheckbox"]        = ("Только предпросмотр", "Preview only (dry run)"),
        ["PreviewButton"]         = ("Предпросмотр", "Preview"),
        ["SortButton"]            = ("Сортировать", "Sort"),
        ["CancelButton"]          = ("Отмена", "Cancel"),
        ["UndoButton"]            = ("Отменить последнюю", "Undo last"),
        ["SettingsButton"]        = ("Настройки", "Settings"),
        ["LogHeader"]             = ("Журнал / результаты", "Log / Results"),
        ["LogFilterPlaceholder"]  = ("Фильтр журнала...", "Filter log..."),
        ["Ready"]                 = ("Готов к работе", "Ready"),
        ["DoneSummary"]           = ("Готово. Перемещено: {0}, пропущено: {1}, ошибок: {2}.",
                                     "Done. Moved: {0}, skipped: {1}, errors: {2}."),
        ["DryDoneSummary"]        = ("Предпросмотр завершён. К перемещению: {0}, пропущено: {1}.",
                                     "Preview complete. Would move: {0}, would skip: {1}."),
        ["UndoSummary"]           = ("Откат завершён. Возвращено: {0}, пропущено: {1}, ошибок: {2}.",
                                     "Undo complete. Reverted: {0}, skipped: {1}, errors: {2}."),
        ["NoFiles"]               = ("В папке нет файлов для сортировки.",
                                     "No files to sort in this folder."),
        ["FolderNotFound"]        = ("Папка не найдена: {0}", "Folder not found: {0}"),
        ["ChooseFolderFirst"]     = ("Сначала выберите папку.", "Choose a folder first."),
        ["ProgressFormat"]        = ("{0} из {1}", "{0} of {1}"),

        // Sort steps
        ["MoveText"]              = ("{0} → {1}", "{0} → {1}"),
        ["DryMoveText"]           = ("[предпросмотр] {0} → {1}", "[preview] {0} → {1}"),
        ["SkipText"]              = ("Пропуск: {0}", "Skipped: {0}"),
        ["ErrorText"]             = ("Ошибка: {0}", "Error: {0}"),

        // Validation / safety
        ["UnsafeTarget"]          = ("Цель вне выбранной папки — отказ", "Target outside selected folder — refusing"),
        ["RefuseSystemFolder"]    = ("Эту папку сортировать нельзя — это системная папка Windows.",
                                     "Refusing to sort this folder — it is a Windows system folder."),
        ["RefuseDriveRoot"]       = ("Эту папку сортировать нельзя — это корень диска.",
                                     "Refusing to sort this folder — it is a drive root."),
        ["LargeSortConfirm"]      = ("Найдено {0} файлов ({1}). Продолжить сортировку?",
                                     "Found {0} files ({1}). Continue?"),
        ["NoUndoAvailable"]       = ("Нет журнала перемещений — отменять нечего.",
                                     "No move journal — nothing to undo."),

        // Settings tabs
        ["TabCategories"]         = ("Категории", "Categories"),
        ["TabExclusions"]         = ("Исключения", "Exclusions"),
        ["TabBehavior"]           = ("Поведение", "Behavior"),
        ["TabDate"]               = ("Дата", "Date"),

        // Categories tab
        ["ColumnId"]              = ("ID", "ID"),
        ["ColumnNameRu"]          = ("Имя (RU)", "Name (RU)"),
        ["ColumnNameEn"]          = ("Имя (EN)", "Name (EN)"),
        ["ColumnExtensions"]      = ("Расширения (через запятую)", "Extensions (comma-separated)"),
        ["ColumnNameRegex"]       = ("Regex имени (через ;)", "Name regex (semicolon-separated)"),
        ["AddCategory"]           = ("Добавить", "Add"),
        ["RemoveCategory"]        = ("Удалить", "Remove"),
        ["CategoriesHelp"]        = ("Каждая категория — отдельная папка. Расширения через запятую: .jpg, .png. Regex (необязательно) — для имени файла, через ;",
                                     "Each category becomes a folder. Extensions, comma-separated: .jpg, .png. Regex (optional) is matched against the filename, semicolon-separated."),

        // Exclusions tab
        ["ExclusionsHelp"]        = ("Файлы с этими расширениями не трогаются.",
                                     "Files with these extensions will be skipped."),
        ["AddExt"]                = ("Добавить", "Add"),
        ["RemoveExt"]             = ("Удалить", "Remove"),
        ["ExtPlaceholder"]        = (".lnk", ".lnk"),

        // Behavior tab
        ["DefaultRecursive"]      = ("По умолчанию рекурсивно", "Recursive by default"),
        ["DefaultDryRun"]         = ("По умолчанию только предпросмотр", "Preview-only by default"),
        ["UseOtherFolder"]        = ("Создавать папку «Прочее» для неизвестных расширений",
                                     "Create \"Other\" folder for unknown extensions"),
        ["OtherNameRuLabel"]      = ("Имя «Прочее» (RU):", "Other folder name (RU):"),
        ["OtherNameEnLabel"]      = ("Имя «Прочее» (EN):", "Other folder name (EN):"),
        ["CollisionLabel"]        = ("При совпадении имён:", "On name collision:"),
        ["CollisionSuffix"]       = ("Добавлять (1), (2), ...", "Append (1), (2), ..."),
        ["CollisionSkip"]         = ("Пропускать", "Skip"),
        ["CollisionReplaceNewer"] = ("Заменять, если новее", "Replace if newer"),
        ["CollisionAsk"]          = ("Спрашивать каждый раз", "Ask each time"),

        // Date tab
        ["SortModeLabel"]         = ("Группировать файлы по:", "Group files by:"),
        ["SortModeExtension"]     = ("Расширению (по умолчанию)", "Extension (default)"),
        ["SortModeYear"]          = ("Году изменения", "Modification year"),
        ["SortModeYearMonth"]     = ("Году и месяцу изменения", "Modification year & month"),
        ["SortModeExtAndYear"]    = ("Расширению и году внутри", "Extension and year inside"),
        ["DateHelp"]              = ("Дата берётся из времени последнего изменения файла (LastWriteTime).",
                                     "Date comes from each file's LastWriteTime (last modification time)."),

        // Collision dialog
        ["CollisionDialogTitle"]  = ("Файл уже существует", "File already exists"),
        ["CollisionDialogText"]   = ("Файл «{0}» уже есть в папке назначения.\nЧто сделать?",
                                     "A file named \"{0}\" already exists at the destination.\nWhat do you want to do?"),
        ["ApplyToAll"]            = ("Применить ко всем", "Apply to all"),

        // Common
        ["OK"]                    = ("ОК", "OK"),
        ["Cancel"]                = ("Отмена", "Cancel"),
        ["Save"]                  = ("Сохранить", "Save"),
        ["Yes"]                   = ("Да", "Yes"),
        ["No"]                    = ("Нет", "No"),

        // Settings validation
        ["ValidationEmptyId"]     = ("ID категории не может быть пустым.", "Category ID cannot be empty."),
        ["ValidationEmptyName"]   = ("Хотя бы одно имя (RU или EN) должно быть задано.",
                                     "At least one name (RU or EN) must be set."),
        ["ValidationDuplicateId"] = ("ID «{0}» повторяется.", "ID \"{0}\" is duplicated."),
        ["ValidationBadRegex"]    = ("Некорректный regex в категории «{0}»: {1}",
                                     "Invalid regex in category \"{0}\": {1}"),

        // Theme
        ["ThemeSystem"]           = ("Системная", "System"),
        ["ThemeLight"]            = ("Светлая",   "Light"),
        ["ThemeDark"]             = ("Тёмная",    "Dark"),

        // Status bar
        ["VersionLabel"]          = ("v{0} · сгенерировано Claude Sonnet 4.6",
                                     "v{0} · generated by Claude Sonnet 4.6"),
    };
}
