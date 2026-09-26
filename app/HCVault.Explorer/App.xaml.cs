using System.Windows;
using Microsoft.Win32;

namespace HCVault.Explorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnExit(e);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // theme flips land here as a "General" preference change
        if (e.Category == UserPreferenceCategory.General)
            ApplySystemTheme();
    }

    private static bool IsSystemThemeDark() =>
        Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int useLight && useLight == 0;

    private void ApplySystemTheme()
    {
        // App.xaml merges the Fluent theme dictionary at index 0; swap it for the
        // variant matching the OS light/dark setting. Fluent.xaml itself is the
        // light variant, so a failed swap just keeps the light look.
        string variant = IsSystemThemeDark() ? "Dark" : "Light";
        try
        {
            Resources.MergedDictionaries[0] = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{variant}.xaml",
                    UriKind.Absolute),
            };
        }
        catch (Exception)
        {
            // keep the dictionary merged from App.xaml (light)
        }
    }
}
