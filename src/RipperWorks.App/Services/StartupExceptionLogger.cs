using System.IO;
using System.Text;
using RipperWorks.Infrastructure;

namespace RipperWorks.App.Services;

public sealed class StartupExceptionLogger
{
    private readonly object _gate = new();
    private readonly string _logsDirectory;

    public StartupExceptionLogger(RipperWorksPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logsDirectory = paths.LogsDirectory;
    }

    public string? LastLogPath { get; private set; }

    public string Log(string source, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            Directory.CreateDirectory(_logsDirectory);
            var timestamp = DateTimeOffset.Now;
            var path = Path.Combine(
                _logsDirectory,
                $"startup-{timestamp:yyyyMMdd-HHmmss-fff}.log");
            var report = new StringBuilder()
                .AppendLine($"Timestamp: {timestamp:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"Process: {Environment.ProcessPath}")
                .AppendLine($"Framework: {Environment.Version}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(path, report, new UTF8Encoding(false));
            LastLogPath = path;
            return path;
        }
    }

    public string LogDownloaderStage(
        string stage,
        Guid entryId,
        string entryName,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            Directory.CreateDirectory(_logsDirectory);
            var path = Path.Combine(
                _logsDirectory,
                $"downloader-{DateTimeOffset.Now:yyyyMMdd}.log");
            var report = new StringBuilder()
                .AppendLine(
                    $"{DateTimeOffset.Now:O} [{stage}] " +
                    $"EntryId={entryId:N}; Name={entryName}");
            if (exception is not null)
                report.AppendLine(exception.ToString());
            File.AppendAllText(
                path,
                report.ToString(),
                new UTF8Encoding(false));
            LastLogPath = path;
            return path;
        }
    }
}
