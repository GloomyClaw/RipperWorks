using System.Globalization;
using System.Text;
using ExcelDataReader;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed record DownloaderTableOptions(
    string? EncodingName = null,
    char? Delimiter = null,
    int? HeaderRow = null,
    int? DataStartRow = null);

public sealed record DownloaderSheetInfo(
    string Name,
    int RowCount);

public sealed record DownloaderImportSheet(
    string Name,
    DownloaderTableOptions Options,
    IReadOnlyDictionary<string, string> Mapping);

public sealed record DownloaderImportSummary(
    int FoundRows,
    int NewRecords,
    int UpdatedRecords,
    int UnchangedDuplicates,
    int Skipped,
    int MissingNames,
    int MissingUrls,
    int FinalMergeTotal,
    int FinalReplaceTotal);

public sealed class DownloaderTableImporter
{
    private static readonly Dictionary<string, string[]> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = ["название", "название мода", "name", "mod name"],
            ["Url"] = ["основной url", "url", "ссылка", "nexus url", "mod url", "download url"],
            ["AdditionalUrl"] = ["дополнительный url", "additional url"],
            ["Category"] = ["категория", "category"],
            ["Source"] = ["источник", "source"],
            ["Author"] = ["автор", "author"],
            ["NexusModId"] = ["modid", "mod_id", "mod id", "nexus mod id", "id мода"],
            ["NexusFileId"] = ["fileid", "file_id", "file id", "nexus file id", "id файла"],
            ["Version"] = ["версия", "version"],
            ["Status"] = ["статус", "status"],
            ["DownloadOrder"] = ["порядок загрузки", "download order"],
            ["CatalogId"] = ["id каталога", "catalog id"],
            ["DecisionGroup"] = ["решение каталога", "решение", "группа"]
        };

    static DownloaderTableImporter() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<string> GetSheetNames(string filePath) =>
        ReadSheets(filePath, null).Select(sheet => sheet.Name).ToArray();

    public IReadOnlyList<DownloaderSheetInfo> GetSheetInfos(
        string filePath,
        DownloaderTableOptions? options = null) =>
        ReadSheets(filePath, options)
            .Select(sheet =>
            {
                var header = options?.HeaderRow ??
                    FindHeaderRow(sheet.Rows);
                var start = options?.DataStartRow ?? header + 1;
                return new DownloaderSheetInfo(
                    sheet.Name,
                    Math.Max(0, sheet.Rows.Count - start));
            })
            .ToArray();

    public DownloaderImportPreview Preview(
        string filePath,
        string? sheetName = null,
        DownloaderTableOptions? options = null)
    {
        options ??= new();
        var sheet = SelectSheet(
            ReadSheets(filePath, options),
            sheetName);
        if (sheet.Rows.Count == 0)
            return new(sheet.Name, [], [], new Dictionary<string, string>(), true);
        var headerIndex = Math.Clamp(
            options.HeaderRow ?? FindHeaderRow(sheet.Rows),
            0,
            sheet.Rows.Count - 1);
        var dataStart = Math.Clamp(
            options.DataStartRow ?? headerIndex + 1,
            headerIndex + 1,
            sheet.Rows.Count);
        var headers = UniqueHeaders(sheet.Rows[headerIndex]);
        var mapping = SuggestMapping(headers);
        return new(
            sheet.Name,
            headers,
            sheet.Rows.Skip(dataStart)
                .Take(30)
                .Select(row => (IReadOnlyList<string>)Pad(row, headers.Count))
                .ToArray(),
            mapping,
            !mapping.Values.Contains("Name") ||
            !mapping.Values.Contains("Url"),
            sheet.EncodingName,
            sheet.Delimiter,
            headerIndex,
            dataStart);
    }

    public IReadOnlyList<DownloaderEntry> Import(
        string filePath,
        string? sheetName,
        IReadOnlyDictionary<string, string> mapping,
        DownloaderTableOptions? options = null)
    {
        options ??= new();
        var sheet = SelectSheet(
            ReadSheets(filePath, options),
            sheetName);
        if (sheet.Rows.Count == 0)
            return [];
        var headerIndex = Math.Clamp(
            options.HeaderRow ?? FindHeaderRow(sheet.Rows),
            0,
            sheet.Rows.Count - 1);
        var dataStart = Math.Clamp(
            options.DataStartRow ?? headerIndex + 1,
            headerIndex + 1,
            sheet.Rows.Count);
        var headers = UniqueHeaders(sheet.Rows[headerIndex]);
        var effectiveMapping = mapping.Count == 0
            ? SuggestMapping(headers)
            : mapping;
        var columns = effectiveMapping
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.Value) &&
                headers.Any(header => string.Equals(
                    header,
                    pair.Key,
                    StringComparison.OrdinalIgnoreCase)))
            .GroupBy(
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => IndexOf(headers, group.First().Key),
                StringComparer.OrdinalIgnoreCase);
        var missingRequired = new[] { "Name", "Url" }
            .Where(field => !columns.ContainsKey(field))
            .ToArray();
        if (missingRequired.Length > 0)
        {
            throw new InvalidDataException(
                $"Required mapping is missing: {string.Join(", ", missingRequired)}.");
        }
        string Get(IReadOnlyList<string> row, string field) =>
            columns.TryGetValue(field, out var index) &&
            index >= 0 &&
            index < row.Count
                ? row[index].Trim()
                : string.Empty;

        var result = new List<DownloaderEntry>();
        foreach (var raw in sheet.Rows.Skip(dataStart))
        {
            var row = Pad(raw, headers.Count);
            var name = Get(row, "Name");
            var url = Get(row, "Url");
            if (row.All(string.IsNullOrWhiteSpace) ||
                string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(url))
            {
                continue;
            }
            var entry = new DownloaderEntry
            {
                Number = result.Count + 1,
                Name = string.IsNullOrWhiteSpace(name)
                    ? NameFromUrl(url)
                    : name,
                Category = ValueOrUnknown(Get(row, "Category")),
                Source = ParseSource(Get(row, "Source"), url),
                Author = Get(row, "Author"),
                Url = url,
                AdditionalUrl = Get(row, "AdditionalUrl"),
                Version = Get(row, "Version"),
                CatalogId = Get(row, "CatalogId"),
                DecisionGroup = Get(row, "DecisionGroup"),
                Status = string.IsNullOrWhiteSpace(url)
                    ? DownloaderStatus.ManualActionRequired
                    : DownloaderStatus.Added,
                IsSelected = false
            };
            if (long.TryParse(
                    Get(row, "NexusModId"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var modId))
            {
                entry.NexusModId = modId;
            }
            if (long.TryParse(
                    Get(row, "NexusFileId"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var fileId))
            {
                entry.NexusFileId = fileId;
            }
            if (int.TryParse(
                    Get(row, "DownloadOrder"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var order))
            {
                entry.DownloadOrder = order;
            }
            if (DownloaderLinkParser.TryParseNexusPage(
                    url,
                    out var nexus) &&
                nexus is not null)
            {
                entry.Source = DownloaderSource.Nexus;
                entry.GameDomain = nexus.GameDomain;
                entry.NexusModId = nexus.ModId;
                entry.NexusFileId ??= nexus.FileId;
            }
            result.Add(entry);
        }
        return result;
    }

    public IReadOnlyList<DownloaderEntry> ImportSheets(
        string filePath,
        IReadOnlyList<DownloaderImportSheet> sheets)
    {
        var prepared = new List<DownloaderEntry>();
        foreach (var sheet in sheets)
        {
            prepared.AddRange(Import(
                filePath,
                sheet.Name,
                sheet.Mapping,
                sheet.Options));
        }
        return prepared;
    }

    public async Task ExportCsvAsync(
        string filePath,
        IEnumerable<DownloaderEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(
            filePath,
            false,
            new UTF8Encoding(true));
        await writer.WriteLineAsync(
            "Название,Категория,Источник,Автор,URL,mod_id,file_id,Версия,Статус,Путь архива");
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                entry.Name,
                entry.Category,
                entry.Source.ToString(),
                entry.Author,
                entry.Url,
                entry.NexusModId?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty,
                entry.NexusFileId?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Version,
                entry.Status.ToString(),
                entry.LocalArchivePath
            };
            await writer.WriteLineAsync(string.Join(",", values.Select(Escape)));
        }
    }

    private static IReadOnlyList<Sheet> ReadSheets(
        string path,
        DownloaderTableOptions? options)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".csv" or ".tsv")
            return [ReadDelimited(
                path,
                options,
                extension == ".tsv" ? '\t' : null)];
        if (extension is not ".xlsx" and not ".xlsm" and not ".xls")
            throw new NotSupportedException($"Unsupported table format: {extension}");
        var result = new List<Sheet>();
        using var stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(
            stream,
            new ExcelReaderConfiguration
            {
                FallbackEncoding = Encoding.GetEncoding(1251)
            });
        do
        {
            var rows = new List<List<string>>();
            while (reader.Read())
            {
                var row = new List<string>();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row.Add(Convert.ToString(
                        reader.GetValue(index),
                        CultureInfo.InvariantCulture) ?? string.Empty);
                }
                Trim(row);
                rows.Add(row);
            }
            result.Add(new Sheet(
                reader.Name ?? $"Sheet {result.Count + 1}",
                rows,
                string.Empty,
                null));
        }
        while (reader.NextResult());
        return result;
    }

    private static Sheet ReadDelimited(
        string path,
        DownloaderTableOptions? options,
        char? knownDelimiter)
    {
        var encoding = ResolveEncoding(path, options?.EncodingName);
        var text = File.ReadAllText(path, encoding);
        var delimiter = options?.Delimiter ??
            knownDelimiter ??
            DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length &&
                    text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == delimiter && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n' && !quoted)
            {
                if (character == '\r' &&
                    index + 1 < text.Length &&
                    text[index + 1] == '\n')
                {
                    index++;
                }
                row.Add(field.ToString());
                field.Clear();
                Trim(row);
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            Trim(row);
            rows.Add(row);
        }
        return new(
            Path.GetFileNameWithoutExtension(path),
            rows,
            EncodingLabel(encoding),
            delimiter);
    }

    private static Dictionary<string, string> SuggestMapping(
        IReadOnlyList<string> headers)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (field, aliases) in Aliases)
        {
            var match = headers.FirstOrDefault(header =>
                aliases.Any(alias =>
                    Normalize(header) == Normalize(alias)));
            if (match is not null)
                result[match] = field;
        }
        return result;
    }

    private static Sheet SelectSheet(
        IReadOnlyList<Sheet> sheets,
        string? sheetName)
    {
        if (sheets.Count == 0)
            throw new InvalidDataException("The table does not contain any sheets.");
        if (string.IsNullOrWhiteSpace(sheetName))
            return sheets[0];
        return sheets.FirstOrDefault(sheet =>
                   string.Equals(
                       sheet.Name,
                       sheetName,
                       StringComparison.OrdinalIgnoreCase)) ??
               throw new InvalidDataException(
                   $"Sheet '{sheetName}' was not found.");
    }

    private static int FindHeaderRow(IReadOnlyList<List<string>> rows)
    {
        var bestIndex = 0;
        var bestScore = -1;
        for (var index = 0; index < Math.Min(rows.Count, 40); index++)
        {
            var score = rows[index].Count(value =>
                Aliases.Values.SelectMany(values => values)
                    .Any(alias => Normalize(alias) == Normalize(value)));
            if (score <= bestScore)
                continue;
            bestScore = score;
            bestIndex = index;
        }
        return bestIndex;
    }

    private static List<string> UniqueHeaders(IReadOnlyList<string> row)
    {
        var counts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        for (var index = 0; index < row.Count; index++)
        {
            var header = string.IsNullOrWhiteSpace(row[index])
                ? $"Column {index + 1}"
                : row[index].Trim();
            counts.TryGetValue(header, out var count);
            count++;
            counts[header] = count;
            result.Add(count == 1 ? header : $"{header} ({count})");
        }
        return result;
    }

    private static List<string> Pad(
        IReadOnlyList<string> row,
        int count) =>
        row.Concat(Enumerable.Repeat(
                string.Empty,
                Math.Max(0, count - row.Count)))
            .Take(count)
            .ToList();

    private static void Trim(List<string> row)
    {
        while (row.Count > 0 && string.IsNullOrWhiteSpace(row[^1]))
            row.RemoveAt(row.Count - 1);
    }

    private static Encoding ResolveEncoding(
        string path,
        string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim().ToLowerInvariant() switch
            {
                "windows-1251" or "cp1251" => Encoding.GetEncoding(1251),
                "utf-8 bom" or "utf-8-bom" => new UTF8Encoding(true),
                _ => new UTF8Encoding(false)
            };
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251);
        }
    }

    private static string EncodingLabel(Encoding encoding) =>
        encoding.CodePage == 1251
            ? "Windows-1251"
            : encoding.GetPreamble().Length > 0
                ? "UTF-8 BOM"
                : "UTF-8";

    private static char DetectDelimiter(string text)
    {
        var lines = text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Take(20)
            .ToArray();
        return new[] { ';', ',', '\t' }
            .OrderByDescending(delimiter =>
                lines.Sum(line => line.Count(character =>
                    character == delimiter)))
            .First();
    }

    private static DownloaderSource ParseSource(
        string source,
        string url)
    {
        if (DownloaderLinkParser.TryParseNexusPage(url, out _) ||
            string.Equals(
                source,
                "Nexus",
                StringComparison.OrdinalIgnoreCase))
        {
            return DownloaderSource.Nexus;
        }
        return DownloaderSource.Import;
    }

    private static string NameFromUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.Segments.Last().Trim('/'))
            : "Не определено";

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Не определено" : value;

    private static string Escape(string value) =>
        value.ContainsAny([',', '"', '\r', '\n'])
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    public static string NormalizeHeader(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е')
            .Select(character =>
                character is '_' or '-' or '.'
                    ? ' '
                    : character)
            .ToArray());
        return string.Join(
            " ",
            normalized.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Normalize(string value) =>
        NormalizeHeader(value);

    private static int IndexOf(
        IReadOnlyList<string> values,
        string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(
                    values[index],
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private sealed record Sheet(
        string Name,
        List<List<string>> Rows,
        string EncodingName,
        char? Delimiter);
}

public static class DownloaderImportPlanner
{
    public static DownloaderImportSummary Analyze(
        IReadOnlyList<DownloaderEntry> existing,
        IReadOnlyList<DownloaderEntry> imported,
        int skipped = 0)
    {
        var existingByKey = existing
            .GroupBy(DownloaderImportIdentity.GetKey)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var distinctImported = imported
            .GroupBy(DownloaderImportIdentity.GetKey)
            .Select(group => group.Last())
            .ToArray();
        var added = 0;
        var updated = 0;
        var unchanged = imported.Count - distinctImported.Length;
        foreach (var entry in distinctImported)
        {
            var key = DownloaderImportIdentity.GetKey(entry);
            if (!existingByKey.TryGetValue(key, out var current))
            {
                added++;
                continue;
            }
            if (Equivalent(current, entry))
                unchanged++;
            else
                updated++;
        }
        return new(
            imported.Count,
            added,
            updated,
            unchanged,
            skipped,
            imported.Count(entry =>
                string.IsNullOrWhiteSpace(entry.Name)),
            imported.Count(entry =>
                string.IsNullOrWhiteSpace(entry.Url)),
            existing.Count + added,
            distinctImported.Length);
    }

    private static bool Equivalent(
        DownloaderEntry first,
        DownloaderEntry second) =>
        string.Equals(first.Name, second.Name, StringComparison.Ordinal) &&
        string.Equals(
            first.Category,
            second.Category,
            StringComparison.Ordinal) &&
        string.Equals(first.Author, second.Author, StringComparison.Ordinal) &&
        string.Equals(first.Version, second.Version, StringComparison.Ordinal) &&
        string.Equals(
            first.AdditionalUrl,
            second.AdditionalUrl,
            StringComparison.Ordinal) &&
        first.Source == second.Source &&
        first.DownloadOrder == second.DownloadOrder &&
        string.Equals(
            first.CatalogId,
            second.CatalogId,
            StringComparison.Ordinal) &&
        string.Equals(
            first.DecisionGroup,
            second.DecisionGroup,
            StringComparison.Ordinal);
}

internal static class StringImportExtensions
{
    public static bool ContainsAny(
        this string value,
        IReadOnlyList<char> characters) =>
        characters.Any(value.Contains);
}
