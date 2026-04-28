using System.Diagnostics;
using System.Reflection;

namespace FileSorter;

/// <summary>Single source of truth for the version string shown in the UI.</summary>
public static class VersionInfo
{
    /// <summary>Short "1.1.0" form for the status bar.</summary>
    public static string Display
    {
        get
        {
            var asm  = Assembly.GetEntryAssembly();
            var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // "1.1.0+sha" → "1.1.0"
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            var v = asm?.GetName().Version;
            if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
            return "0.0.0";
        }
    }
}
