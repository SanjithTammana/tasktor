using System.Globalization;
using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Export;

namespace Tasktor.Core.Storage;

/// <summary>What a table needs beyond the default export (U2 D-S7).</summary>
public enum ExportGate
{
    Default,

    /// <summary>Raw T0 events (include window titles): <c>--include-raw</c>.</summary>
    Raw,

    /// <summary>T1 capture content: <c>--include-capture</c>.</summary>
    Capture,
}

/// <summary>One exportable table: columns and a row producer (values: string, numbers, bool, timestamps, null).</summary>
public sealed record ExportTableSource(string Name, IReadOnlyList<string> Columns, Func<DateTimeOffset?, IEnumerable<object?[]>> Rows, ExportGate Gate = ExportGate.Default);

public sealed record ExportOptions
{
    public required string Folder { get; init; }

    public required DateTimeOffset Now { get; init; }

    public required string Version { get; init; }

    public string? Since { get; init; }

    /// <summary><c>csv</c>, <c>jsonl</c> or <c>both</c>.</summary>
    public string Format { get; init; } = "both";

    public bool IncludeRaw { get; init; }

    public bool IncludeCapture { get; init; }
}

public sealed record ExportResult(string Folder, ExportManifest Manifest, bool CloudFolder);

/// <summary>
/// <c>tasktor export data</c> (U2 D-S7): one CSV and/or JSONL file per table plus <c>manifest.json</c> (<c>tasktor.export/1</c>).
/// Raw titles and T1 content only with their flags. Never the install key, consents, plugin secrets or index files.
/// Written with <see cref="Utf8JsonWriter"/> (no reflection). Runs off the loop.
/// </summary>
public sealed class DataExporter
{
    private readonly List<ExportTableSource> _tables = [];

    public IReadOnlyList<ExportTableSource> Tables => _tables;

    public void Register(ExportTableSource table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _tables.RemoveAll(t => t.Name == table.Name);
        _tables.Add(table);
    }

    /// <summary>Default destination: <c>Documents\Tasktor exports\tasktor-export-&lt;time&gt;</c>.</summary>
    public static string DefaultFolder(DateTimeOffset now) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasktor exports", "tasktor-export-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>A destination inside a cloud-synced folder (OneDrive, Dropbox, Google Drive) gets a warning before writing.</summary>
    public static bool IsCloudSynced(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var full = Path.GetFullPath(folder);
        foreach (var env in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } root && full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var part in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) || part.Equals("Dropbox", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) || part.Equals("My Drive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public ExportResult Export(ExportOptions o, DateTimeOffset? since)
    {
        ArgumentNullException.ThrowIfNull(o);
        Directory.CreateDirectory(o.Folder);
        var csv = o.Format is "csv" or "both";
        var jsonl = o.Format is "jsonl" or "both";
        if (!csv && !jsonl)
        {
            throw new ArgumentException("format must be csv, jsonl or both");
        }

        var tables = new List<ExportTable>();
        var leftOut = new List<string>();
        foreach (var t in _tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (t.Gate == ExportGate.Raw && !o.IncludeRaw)
            {
                leftOut.Add(t.Name + ": needs --include-raw");
                continue;
            }

            if (t.Gate == ExportGate.Capture && !o.IncludeCapture)
            {
                leftOut.Add(t.Name + ": needs --include-capture");
                continue;
            }

            var files = new List<string>();
            long rows = 0;
            StreamWriter? c = null;
            FileStream? j = null;
            Utf8JsonWriter? jw = null;
            try
            {
                if (csv)
                {
                    c = new StreamWriter(Path.Combine(o.Folder, t.Name + ".csv"), false, new UTF8Encoding(false));
                    c.Write(string.Join(",", t.Columns.Select(Csv)));
                    c.Write("\r\n");
                    files.Add(t.Name + ".csv");
                }

                if (jsonl)
                {
                    j = new FileStream(Path.Combine(o.Folder, t.Name + ".jsonl"), FileMode.Create);
                    files.Add(t.Name + ".jsonl");
                }

                foreach (var row in t.Rows(since))
                {
                    rows++;
                    if (c is not null)
                    {
                        c.Write(string.Join(",", row.Select(v => Csv(Text(v)))));
                        c.Write("\r\n");
                    }

                    if (j is not null)
                    {
                        jw ??= new Utf8JsonWriter(j);
                        jw.Reset(j);
                        jw.WriteStartObject();
                        for (var i = 0; i < t.Columns.Count; i++)
                        {
                            WriteValue(jw, t.Columns[i], i < row.Length ? row[i] : null);
                        }

                        jw.WriteEndObject();
                        jw.Flush();
                        j.WriteByte((byte)'\n');
                    }
                }
            }
            finally
            {
                c?.Dispose();
                jw?.Dispose();
                j?.Dispose();
            }

            tables.Add(new ExportTable { Name = t.Name, Schema = ContractIds.Export + "#" + t.Name, Rows = rows, Files = files, Columns = t.Columns });
        }

        var manifest = new ExportManifest
        {
            ExportedAt = o.Now,
            TasktorVersion = o.Version,
            Since = o.Since,
            Format = o.Format,
            Tables = tables,
            LeftOut = leftOut.Count == 0 ? null : leftOut,
        };
        File.WriteAllBytes(Path.Combine(o.Folder, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ContractsJsonContext.Indented.ExportManifest));
        return new ExportResult(o.Folder, manifest, IsCloudSynced(o.Folder));
    }

    private static string Text(object? v) => v switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        DateTimeOffset d => d.ToUniversalTime().ToString(UtcTimestampConverter.Format, CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };

    private static void WriteValue(Utf8JsonWriter w, string name, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNull(name);
                break;
            case string s:
                w.WriteString(name, s);
                break;
            case bool b:
                w.WriteBoolean(name, b);
                break;
            case int i:
                w.WriteNumber(name, i);
                break;
            case long l:
                w.WriteNumber(name, l);
                break;
            case double d:
                w.WriteNumber(name, d);
                break;
            case float f:
                w.WriteNumber(name, f);
                break;
            case DateTimeOffset d:
                w.WriteString(name, d.ToUniversalTime().ToString(UtcTimestampConverter.Format, CultureInfo.InvariantCulture));
                break;
            default:
                w.WriteString(name, Text(v));
                break;
        }
    }

    private static string Csv(string s) =>
        s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) || s.Contains('\n', StringComparison.Ordinal) || s.Contains('\r', StringComparison.Ordinal)
            ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : s;
}
