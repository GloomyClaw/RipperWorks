using System.IO;
using RipperWorks.Core;
using RipperWorks.Infrastructure;

namespace RipperWorks.App.Services;

/// <summary>
/// Settings JSON save orchestration for presentation.
/// </summary>
public static class SettingsSaveFlow
{
    public static async Task SaveAsync(
        SettingsSession session,
        LocalizationService localization,
        ThemeService theme,
        IUserDialogService dialogs,
        string gameRoot,
        string libraryRoot,
        string downloaderTempRoot,
        int concurrentDownloads,
        NexusBrowserMode nexusBrowser,
        string language,
        string themeName,
        Action<RipperWorksSettings> applySnapshot,
        Action<string> setStatusMessage,
        Action<RipperWorksSettings> raiseSettingsSaved)
    {
        try
        {
            var normalizedLibrary = NormalizeOptionalPath(libraryRoot);
            if (!string.IsNullOrWhiteSpace(normalizedLibrary) &&
                !Directory.Exists(normalizedLibrary))
            {
                if (!dialogs.Confirm(
                        localization.Get("CreateLibrary"),
                        localization.Get("CreateLibraryTitle")))
                    return;
                Directory.CreateDirectory(normalizedLibrary);
            }

            var settings = new RipperWorksSettings
            {
                SchemaVersion = RipperWorksSettingsSchema.CurrentVersion,
                Cyberpunk2077Root = gameRoot,
                LibraryRoot = normalizedLibrary,
                DownloaderTempRoot = NormalizeOptionalPath(downloaderTempRoot),
                ConcurrentDownloads = Math.Clamp(concurrentDownloads, 1, 8),
                NexusBrowser = Enum.IsDefined(nexusBrowser)
                    ? nexusBrowser
                    : NexusBrowserMode.Internal,
                Language = SupportedLanguages.IsSupported(language)
                    ? language
                    : SupportedLanguages.Russian,
                Theme = SupportedThemes.IsSupported(themeName)
                    ? themeName
                    : SupportedThemes.System
            };

            await session.SaveAsync(settings).ConfigureAwait(true);
            applySnapshot(settings);
            localization.SetLanguage(settings.Language);
            theme.Apply(settings.Theme);

            setStatusMessage(localization.Get("SettingsSaved"));
            raiseSettingsSaved(settings);
        }
        catch (Exception exception)
        {
            var message = string.Format(
                localization.Get("SettingsError"),
                exception.Message);
            setStatusMessage(message);
            dialogs.ShowError(message);
        }
    }

    public static string NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
}
