using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AppShelf.App;

public static class Theme
{
    public static bool IsDark { get; private set; }
    public static void Apply(Appearance appearance)
    {
        IsDark = IsDarkFor(appearance);
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

    public static bool IsDarkFor(Appearance appearance)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return appearance == Appearance.Dark || (appearance == Appearance.System && key?.GetValue("AppsUseLightTheme") is int value && value == 0);
    }

    /// <summary>Translucent plate behind a nested folder's previews, readable over acrylic.</summary>
    public static (Brush Fill, Pen Border) FolderPlate(bool dark)
    {
        var fill = new SolidColorBrush(dark ? Color.FromArgb(0x2E, 255, 255, 255) : Color.FromArgb(0xB3, 255, 255, 255));
        var border = new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x1F, 0, 0, 0)), .8);
        fill.Freeze(); border.Freeze();
        return (fill, border);
    }

    /// <summary>Editor colours over Mica: the sidebar shows the backdrop, the content card is a light layer.</summary>
    public static void ApplyBackdropTo(Window window)
    {
        var colors = new Dictionary<string, string>
        {
            ["CanvasBrush"] = "#00000000",
            ["SurfaceBrush"] = IsDark ? "#4C3A3A3A" : "#80FFFFFF",
            ["SelectedBrush"] = IsDark ? "#14FFFFFF" : "#0A000000",
            ["HoverBrush"] = IsDark ? "#0FFFFFFF" : "#08000000",
            ["LineBrush"] = IsDark ? "#1FFFFFFF" : "#14000000"
        };
        foreach (var (name, color) in colors) window.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public static bool ApplyTo(Window window, Appearance appearance)
    {
        var dark = IsDarkFor(appearance);
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
        return dark;
    }
}
