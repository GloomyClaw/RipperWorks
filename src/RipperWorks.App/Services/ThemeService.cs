using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RipperWorks.Core;

namespace RipperWorks.App.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Func<bool> _systemUsesLightTheme;
    private DispatcherTimer? _systemThemeTimer;
    private string _selectedTheme = SupportedThemes.System;
    private string? _lastAppliedTheme;
    private bool _disposed;
    private int _paletteApplyCount;

    public ThemeService()
        : this(SystemUsesLightTheme)
    {
    }

    internal ThemeService(Func<bool> systemUsesLightTheme)
    {
        _systemUsesLightTheme = systemUsesLightTheme ?? SystemUsesLightTheme;
    }

    internal int PaletteApplyCount => _paletteApplyCount;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_systemThemeTimer is not null)
            return;
        WindowTitleBarService.Initialize();
        var application = Application.Current;
        if (application is null)
            return;

        _systemThemeTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            application.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _systemThemeTimer.Tick += SystemThemeTimer_OnTick;
        _systemThemeTimer.Start();
    }

    public string CurrentResolvedTheme { get; private set; } =
        SupportedThemes.Dark;

    public void Apply(string theme)
    {
        _selectedTheme = SupportedThemes.IsSupported(theme)
            ? theme
            : SupportedThemes.System;
        ApplyResolvedPalette(Resolve(_selectedTheme));
    }

    public void RefreshSystemTheme()
    {
        if (_selectedTheme == SupportedThemes.System)
            ApplyResolvedPalette(Resolve(_selectedTheme));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    public void Stop()
    {
        if (_systemThemeTimer is null)
            return;
        _systemThemeTimer.Stop();
        _systemThemeTimer.Tick -= SystemThemeTimer_OnTick;
        _systemThemeTimer = null;
    }

    private void SystemThemeTimer_OnTick(object? sender, EventArgs e) =>
        RefreshSystemTheme();

    private void ApplyResolvedPalette(string resolvedTheme)
    {
        var application = Application.Current;
        if (application is null)
        {
            // Presentation host not ready (unit tests / early composition).
            CurrentResolvedTheme = resolvedTheme;
            _lastAppliedTheme = resolvedTheme;
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var paletteIndex = FindPaletteIndex(dictionaries);
        if (_lastAppliedTheme == resolvedTheme &&
            paletteIndex >= 0 &&
            IsPaletteForTheme(dictionaries[paletteIndex], resolvedTheme))
        {
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = new Uri(
                resolvedTheme == SupportedThemes.Light
                    ? "pack://application:,,,/RipperWorks;component/Themes/Light.xaml"
                    : "pack://application:,,,/RipperWorks;component/Themes/Dark.xaml",
                UriKind.Absolute)
        };

        if (paletteIndex >= 0)
            dictionaries[paletteIndex] = palette;
        else
            dictionaries.Insert(0, palette);

        _paletteApplyCount++;
        _lastAppliedTheme = resolvedTheme;
        CurrentResolvedTheme = resolvedTheme;
        WindowTitleBarService.ApplyToAll(
            resolvedTheme == SupportedThemes.Dark);
    }

    private static bool IsPaletteForTheme(
        ResourceDictionary palette,
        string resolvedTheme)
    {
        var source = palette.Source?.OriginalString;
        if (source is null) return false;
        var expectedFile = resolvedTheme == SupportedThemes.Light
            ? "Themes/Light.xaml"
            : "Themes/Dark.xaml";
        return source.EndsWith(
            expectedFile,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int FindPaletteIndex(
        IList<ResourceDictionary> dictionaries)
    {
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source?.EndsWith(
                    "Themes/Light.xaml",
                    StringComparison.OrdinalIgnoreCase) == true ||
                source?.EndsWith(
                    "Themes/Dark.xaml",
                    StringComparison.OrdinalIgnoreCase) == true)
                return index;
        }
        return -1;
    }

    private string Resolve(string theme) =>
        theme switch
        {
            SupportedThemes.Light => SupportedThemes.Light,
            SupportedThemes.Dark => SupportedThemes.Dark,
            _ => _systemUsesLightTheme()
                ? SupportedThemes.Light
                : SupportedThemes.Dark
        };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}
