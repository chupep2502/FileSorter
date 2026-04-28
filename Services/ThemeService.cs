using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FileSorter.Services;

/// <summary>
/// Theme switcher: keeps a single MergedDictionary slot in App.Resources up to date
/// with either Light.xaml or Dark.xaml. Mode is "system" | "light" | "dark".
/// "system" follows HKCU\...\Personalize\AppsUseLightTheme and tracks changes
/// while the app is running.
/// </summary>
public class ThemeService : INotifyPropertyChanged
{
    public static ThemeService Current { get; } = new();

    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private string _mode = "system";
    /// <summary>"system" | "light" | "dark"</summary>
    public string Mode
    {
        get => _mode;
        private set
        {
            if (_mode == value) return;
            _mode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private ResourceDictionary? _injected;
    private System.Threading.Timer? _systemPollTimer;
    private bool _lastSystemIsLight = true;

    /// <summary>Apply a mode and remember it for future calls.</summary>
    public void Apply(string mode)
    {
        Mode = NormalizeMode(mode);
        ApplyEffectiveTheme();

        // Start/stop the system poll depending on mode.
        if (Mode == "system")
            EnsureSystemWatcher();
        else
            StopSystemWatcher();
    }

    private static string NormalizeMode(string mode) => mode switch
    {
        "light" => "light",
        "dark"  => "dark",
        _       => "system",
    };

    private void ApplyEffectiveTheme()
    {
        var effective = ResolveEffective(Mode);
        var uri = effective == "dark"
            ? new Uri("pack://application:,,,/Themes/Dark.xaml",  UriKind.Absolute)
            : new Uri("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

        var dict = new ResourceDictionary { Source = uri };

        var app = Application.Current;
        if (app == null) return;

        // Remove the previously injected dictionary, if any.
        if (_injected != null && app.Resources.MergedDictionaries.Contains(_injected))
            app.Resources.MergedDictionaries.Remove(_injected);

        app.Resources.MergedDictionaries.Add(dict);
        _injected = dict;

        // Tell DWM to repaint the title bar (non-client area) in dark/light.
        var darkChrome = effective == "dark";
        foreach (Window w in app.Windows)
            TrySetDarkTitleBar(w, darkChrome);
    }

    /// <summary>Apply the dark/light DWM chrome to a window. Safe to call repeatedly.</summary>
    public void ApplyChromeTo(Window window)
    {
        var darkChrome = ResolveEffective(Mode) == "dark";
        TrySetDarkTitleBar(window, darkChrome);
    }

    // --- DWM dark title bar ---
    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 on Win10 build 18985+ and Win11; some early
    // Win10 builds used 19 — we try 20 first, then fall back to 19.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static void TrySetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
            {
                // Window source not yet created — wait for SourceInitialized.
                window.SourceInitialized -= OnSourceInitialized;
                window.SourceInitialized += OnSourceInitialized;
                // Stash the requested mode on the window so the handler can read it.
                window.Tag = dark ? "dwm-dark" : "dwm-light";
                return;
            }
            int value = dark ? 1 : 0;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            if (hr != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
        catch { /* best effort */ }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        var dark = (w.Tag as string) == "dwm-dark";
        TrySetDarkTitleBar(w, dark);
    }

    private string ResolveEffective(string mode)
    {
        if (mode == "light") return "light";
        if (mode == "dark")  return "dark";
        return ReadSystemIsLight() ? "light" : "dark";
    }

    private static bool ReadSystemIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (k?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* ignore — default to light */ }
        return true;
    }

    private void EnsureSystemWatcher()
    {
        if (_systemPollTimer != null) return;
        _lastSystemIsLight = ReadSystemIsLight();
        // Cheap registry read every 2s; only re-applies the dictionary on change.
        _systemPollTimer = new System.Threading.Timer(_ =>
        {
            var isLight = ReadSystemIsLight();
            if (isLight == _lastSystemIsLight) return;
            _lastSystemIsLight = isLight;

            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Mode == "system") ApplyEffectiveTheme();
            }));
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void StopSystemWatcher()
    {
        _systemPollTimer?.Dispose();
        _systemPollTimer = null;
    }
}
