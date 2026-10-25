using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppShelf.App;

public static class Theme
{
    public static bool IsDark { get; private set; }
    public static void Apply(Appearance appearance)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        IsDark = appearance == Appearance.Dark || (appearance == Appearance.System && key?.GetValue("AppsUseLightTheme") is int value && value == 0);
        Application.Current.ThemeMode = appearance switch
        {
            Appearance.Light => ThemeMode.Light,
            Appearance.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        Set("CanvasBrush", IsDark ? "#202020" : "#F3F3F3");
        Set("SurfaceBrush", IsDark ? "#292929" : "#FFFFFF");
        Set("SubtleBrush", IsDark ? "#242424" : "#F9F9F9");
        Set("HoverBrush", IsDark ? "#383838" : "#EBEBEB");
        Set("LineBrush", IsDark ? "#404040" : "#E5E5E5");
        Set("TextBrush", IsDark ? "#FFFFFF" : "#1A1A1A");
        Set("MutedBrush", IsDark ? "#B8B8B8" : "#616161");
        Set("FolderBrush", IsDark ? "#414141" : "#EAEAEA");
        Set("SelectedBrush", IsDark ? "#363F49" : "#E5EEF8");
        if (SystemParameters.HighContrast)
        {
            foreach (var name in new[] { "CanvasBrush", "SurfaceBrush", "SubtleBrush", "FolderBrush" }) Application.Current.Resources[name] = SystemColors.WindowBrush;
            Application.Current.Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["LineBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["HoverBrush"] = SystemColors.HighlightBrush;
        }
    }
    private static void Set(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    public static void ApplyTo(Window window, Appearance appearance)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dark = appearance == Appearance.Dark || (appearance == Appearance.System && key?.GetValue("AppsUseLightTheme") is int value && value == 0);
        window.ThemeMode = appearance switch { Appearance.Dark => ThemeMode.Dark, Appearance.Light => ThemeMode.Light, _ => ThemeMode.System };
        var colors = new Dictionary<string, string>
        {
            ["SurfaceBrush"] = dark ? "#292929" : "#FFFFFF",
            ["TextBrush"] = dark ? "#FFFFFF" : "#1A1A1A",
            ["MutedBrush"] = dark ? "#B8B8B8" : "#616161",
            ["HoverBrush"] = dark ? "#3B3B3B" : "#EEEEEE",
            ["LineBrush"] = dark ? "#484848" : "#DEDEDE"
        };
        foreach (var (name, color) in colors) window.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        if (SystemParameters.HighContrast)
        {
            window.Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            window.Resources["TextBrush"] = SystemColors.WindowTextBrush;
            window.Resources["HoverBrush"] = SystemColors.HighlightBrush;
            window.Resources["LineBrush"] = SystemColors.WindowTextBrush;
        }
    }
}
