using System.Windows;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.App;

/// <summary>Theme management. The app is dark-only since the erdi-erc.de redesign —
/// the AppTheme setting is still accepted (and persisted) for settings.json
/// compatibility but every value now lands on Themes/Dark.xaml. The Apply/IsDark
/// surface is kept so callers do not change.</summary>
public static class ThemeManager
{
    public static void Apply(AppTheme theme)
    {
        const string source = "Themes/Dark.xaml";

        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var uri = merged[i].Source;
            if (uri?.OriginalString is { } path
                && (path.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                merged.RemoveAt(i);
            }
        }

        // Index 0 so a future App.xaml merge cannot shadow the theme brushes.
        merged.Insert(0, new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
    }

    /// <summary>Used for DWM chrome decisions (dark title bar, border color) —
    /// always true now that Light/System resolve to the same dark dictionary.</summary>
    public static bool IsDark(AppTheme theme) => true;
}