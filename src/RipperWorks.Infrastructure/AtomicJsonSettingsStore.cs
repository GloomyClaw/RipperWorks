using System.Text.Json;
using RipperWorks.Core;

namespace RipperWorks.Infrastructure;

public sealed class AtomicJsonSettingsStore : IRipperWorksSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonSettingsStore(RipperWorksPaths paths)
        : this(paths.SettingsPath)
    {
    }

    public AtomicJsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<RipperWorksSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        RipperWorksSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RipperWorksSettings> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var temporaryPath = GetTemporaryPath();
        if (File.Exists(_settingsPath))
        {
            try
            {
                var settings = await ReadAsync(
                    _settingsPath,
                    cancellationToken).ConfigureAwait(false);
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                return settings;
            }
            catch (Exception exception) when (IsCorruptContent(exception))
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        var recovered = await ReadAsync(
                            temporaryPath,
                            cancellationToken).ConfigureAwait(false);
                        MoveToCorruptBackup(_settingsPath);
                        File.Move(temporaryPath, _settingsPath, true);
                        return recovered;
                    }
                    catch (Exception temporaryException)
                        when (IsCorruptContent(temporaryException))
                    {
                        MoveToCorruptBackup(_settingsPath);
                        MoveToCorruptBackup(temporaryPath);
                    }
                }
                else
                {
                    MoveToCorruptBackup(_settingsPath);
                }

                var defaults = new RipperWorksSettings();
                await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
                return defaults;
            }
        }

        if (!File.Exists(temporaryPath))
            return new RipperWorksSettings();

        try
        {
            var recovered = await ReadAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Move(temporaryPath, _settingsPath, true);
            return recovered;
        }
        catch (Exception exception) when (IsCorruptContent(exception))
        {
            MoveToCorruptBackup(temporaryPath);
            var defaults = new RipperWorksSettings();
            await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
    }

    private async Task SaveCoreAsync(
        RipperWorksSettings settings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = GetTemporaryPath();
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _settingsPath, true);
    }

    private static async Task<RipperWorksSettings> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await JsonSerializer.DeserializeAsync<RipperWorksSettings>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException("Settings document is empty.");
        Validate(settings);
        return Normalize(settings);
    }

    private static RipperWorksSettings Normalize(RipperWorksSettings settings) =>
        settings with
        {
            Cyberpunk2077Root = settings.Cyberpunk2077Root?.Trim() ?? string.Empty,
            LibraryRoot = settings.LibraryRoot?.Trim() ?? string.Empty,
            DownloaderTempRoot =
                settings.DownloaderTempRoot?.Trim() ?? string.Empty,
            ConcurrentDownloads = Math.Clamp(
                settings.ConcurrentDownloads,
                1,
                8),
            NexusBrowser = Enum.IsDefined(settings.NexusBrowser)
                ? settings.NexusBrowser
                : NexusBrowserMode.Internal,
            Language = SupportedLanguages.IsSupported(settings.Language)
                ? settings.Language
                : SupportedLanguages.Russian,
            Theme = SupportedThemes.IsSupported(settings.Theme)
                ? settings.Theme
                : SupportedThemes.System
        };

    private static void Validate(RipperWorksSettings settings)
    {
        if (settings.SchemaVersion != RipperWorksSettingsSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported settings schema version: {settings.SchemaVersion}.");
        }
    }

    private string GetTemporaryPath() => _settingsPath + ".tmp";

    private static bool IsCorruptContent(Exception exception) =>
        exception is JsonException or NotSupportedException or InvalidDataException;

    private static void MoveToCorruptBackup(string path)
    {
        if (!File.Exists(path))
            return;

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backup = $"{path}.corrupt-{timestamp}";
        for (var suffix = 2; File.Exists(backup); suffix++)
            backup = $"{path}.corrupt-{timestamp}-{suffix}";
        File.Move(path, backup);
    }
}
