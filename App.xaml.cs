using System.Windows;
using FileSorter.Services;

namespace FileSorter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Load settings before any window opens so language and theme are correct from frame 1.
        var settings = SettingsService.Load();
        LocalizationService.Current.SetLanguage(settings.Language);
        ThemeService.Current.Apply(settings.Theme);
        SettingsService.SetCached(settings);

        // Best-effort: roll out logs older than 30 days so the folder never grows unbounded.
        LogService.CleanupOld(30);
        LogService.Info($"FileSorter {VersionInfo.Display} started.");

        base.OnStartup(e);
    }
}
