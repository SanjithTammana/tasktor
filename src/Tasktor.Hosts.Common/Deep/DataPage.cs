using System.Globalization;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Export;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// U4 D-G3 "Your data" ("What Tasktor knows"): the agent's <c>data</c> pull as the deep window's rows. One section per
/// group with its size, file count, oldest and newest dates and how long it is kept, and a "Forget …" action that is the
/// destructive <c>forget</c> with that group's scope (two-step, D-H4). Empty groups say so and offer nothing to forget.
/// </summary>
public static class DataPage
{
    public static IReadOnlyList<CardRows> Sections(DataReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        return [.. report.Groups.Select(g => Section(g, zone))];
    }

    public static CardRows Section(DataGroup g, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(g);
        var rows = new List<CardRow>();
        if (g.Files == 0)
        {
            rows.Add(new CardRow(RowKind.Text, "Nothing kept."));
            rows.Add(new CardRow(RowKind.Value, "Kept", g.Kept));
            return new CardRows("data." + g.Scope, g.Name, Severity.Normal, null, null, rows, [], []);
        }

        rows.Add(new CardRow(RowKind.Value, "Size", Bytes(g.Bytes)));
        rows.Add(new CardRow(RowKind.Value, "Files", g.Files.ToString(CultureInfo.InvariantCulture)));
        if (g.Oldest is { } oldest)
        {
            rows.Add(new CardRow(RowKind.Value, "Oldest", Day(oldest, zone)));
        }

        if (g.Newest is { } newest)
        {
            rows.Add(new CardRow(RowKind.Value, "Newest", Day(newest, zone)));
        }

        rows.Add(new CardRow(RowKind.Value, "Kept", g.Kept));
        var forget = new CardAction
        {
            Id = "forget",
            Label = g.Scope == "all" ? "Forget everything…" : "Forget this…",
            Action = "forget",
            Target = new Dictionary<string, string> { ["what"] = g.Scope },
            Destructive = true,
        };
        return new CardRows("data." + g.Scope, g.Name, Severity.Normal, null, null, rows, [forget], []);
    }

    /// <summary>Bytes in the units Explorer uses (1 KB = 1024 bytes).</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = Math.Max(0, bytes);
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? ((long)v).ToString(CultureInfo.InvariantCulture) + " bytes" : v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[u];
    }

    private static string Day(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// D-S7 "Your data → Export": the agent writes CSV + JSONL per table and a manifest to a new folder under Documents.
    /// Window titles (<c>events_raw</c>) and decrypted capture records go in only when ticked, and then the agent asks first.
    /// </summary>
    public static CardAction Export(bool includeRaw, bool includeCapture)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (includeRaw)
        {
            args["include_raw"] = "true";
        }

        if (includeCapture)
        {
            args["include_capture"] = "true";
        }

        return new CardAction { Id = "export", Label = "Export my data", Action = "export.data", Args = args.Count == 0 ? null : args };
    }
}
