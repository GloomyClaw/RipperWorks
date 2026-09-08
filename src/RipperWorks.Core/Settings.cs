namespace RipperWorks.Core;

public static class RipperWorksSettingsSchema
{
    public const int CurrentVersion = 1;
}

public static class SupportedLanguages
{
    public const string Russian = "ru-RU";
    public const string English = "en-US";

    public static bool IsSupported(string? value) =>
        value is Russian or English;
}

public static class SupportedThemes
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static bool IsSupported(string? value) =>
        value is System or Light or Dark;
}

public enum NexusBrowserMode
{
    Internal,
    External
}

public sealed record RipperWorksSettings
{
    public int SchemaVersion { get; init; } =
        RipperWorksSettingsSchema.CurrentVersion;
    public string Cyberpunk2077Root { get; init; } = string.Empty;
    public string LibraryRoot { get; init; } = string.Empty;
    public string Language { get; init; } = SupportedLanguages.Russian;
    public string Theme { get; init; } = SupportedThemes.System;
    public string DownloaderTempRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "RipperWorks",
        "Downloads");
    public int ConcurrentDownloads { get; init; } = 2;
    public NexusBrowserMode NexusBrowser { get; init; } =
        NexusBrowserMode.Internal;
}

public interface ISettingsSnapshotProvider
{
    RipperWorksSettings Current { get; }
    event EventHandler<RipperWorksSettings>? SnapshotChanged;
}
