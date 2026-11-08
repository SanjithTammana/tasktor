using System.Text.Json;
using System.Text.RegularExpressions;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Plugins;
using Tasktor.Core.Time;

namespace Tasktor.Core.Plugins;

/// <summary>Checks <c>plugin.json</c> and plugin output against the contract (U1 D-H7). Pure.</summary>
public static partial class PluginValidation
{
    public const int MaxOutputBytes = 64 * 1024;
    public const int MaxCards = 3;
    public const int MaxActionsPerCard = 3;
    public const int MaxSparklinePoints = 60;
    public const int MaxTableRows = 12;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    /// <summary>A usable plugin id (also its folder name).</summary>
    public static bool ValidId(string id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,31}$")]
    private static partial Regex CardIdPattern();

    /// <summary>Null when the manifest is usable; otherwise why not.</summary>
    public static string? CheckManifest(PluginManifest m, string folderName, Version tasktorVersion)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Contract != 1)
        {
            return "contract " + m.Contract + " is not supported";
        }

        if (!IdPattern().IsMatch(m.Id))
        {
            return "id must be lower-case letters, digits, '.', '_' or '-'";
        }

        if (!string.Equals(m.Id, folderName, StringComparison.Ordinal))
        {
            return "id \"" + m.Id + "\" must equal its folder name \"" + folderName + "\"";
        }

        if (m.Command.Count == 0 || m.Command.Any(string.IsNullOrWhiteSpace))
        {
            return "command is empty";
        }

        if (m.Interval != "manual" && (!DurationText.TryParse(m.Interval, out var every) || every < MinInterval))
        {
            return "interval must be ≥ 30s or \"manual\"";
        }

        if (!DurationText.TryParse(m.Timeout, out var t) || t <= TimeSpan.Zero || t > MaxTimeout)
        {
            return "timeout must be at most 60s";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in m.Actions ?? [])
        {
            if (a.Class == ActionClass.Destructive)
            {
                return "action " + a.Id + ": plugins may not declare destructive actions";
            }

            if (!ids.Add(a.Id))
            {
                return "action " + a.Id + " is declared twice";
            }
        }

        if (m.MinTasktor is { } min && Version.TryParse(min.Contains('.', StringComparison.Ordinal) ? min : min + ".0", out var need) && need > tasktorVersion)
        {
            return "needs Tasktor " + min + " or newer";
        }

        return null;
    }

    /// <summary>Parse and check a plugin's stdout; returns its cards rewritten for the snapshot, or an error.</summary>
    public static (IReadOnlyList<Card>? Cards, string? Error) CheckOutput(PluginManifest manifest, string stdout, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stdout);
        if (System.Text.Encoding.UTF8.GetByteCount(stdout) > MaxOutputBytes)
        {
            return (null, "output is over 64 KB");
        }

        PluginOutput? output;
        try
        {
            output = JsonSerializer.Deserialize(stdout.Trim(), ContractsJsonContext.Default.PluginOutput);
        }
        catch (JsonException ex)
        {
            return (null, "output is not valid: " + ex.Message.Split('\n')[0]);
        }
        catch (NotSupportedException ex)
        {
            return (null, "output is not valid: " + ex.Message.Split('\n')[0]);
        }

        if (output is null)
        {
            return (null, "no output");
        }

        if (output.Contract != 1)
        {
            return (null, "output contract " + output.Contract + " is not supported");
        }

        if (output.Cards.Count > MaxCards)
        {
            return (null, "more than 3 cards");
        }

        var declared = (manifest.Actions ?? []).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var cards = new List<Card>();
        foreach (var c in output.Cards)
        {
            if (!CardIdPattern().IsMatch(c.Id))
            {
                return (null, "card id \"" + c.Id + "\" is not allowed");
            }

            if ((c.Actions?.Count ?? 0) > MaxActionsPerCard)
            {
                return (null, "card " + c.Id + ": more than 3 actions");
            }

            if (c.Severity is { IsBinding: true })
            {
                return (null, "card " + c.Id + ": plugins send literal values only");
            }

            foreach (var b in c.Blocks)
            {
                if (CheckBlock(b, declared) is { } blockError)
                {
                    return (null, "card " + c.Id + ": " + blockError);
                }
            }

            var actions = new List<CardAction>();
            foreach (var a in c.Actions ?? [])
            {
                if (!declared.ContainsKey(a.Id))
                {
                    return (null, "card " + c.Id + ": action " + a.Id + " is not declared in plugin.json");
                }

                actions.Add(new CardAction
                {
                    Id = a.Id,
                    Label = a.Label,
                    Priority = a.Priority,
                    Action = "plugin.action",
                    Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["plugin"] = manifest.Id },
                    Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = a.Id },
                });
            }

            cards.Add(c with
            {
                Id = "plugin." + manifest.Id + "." + c.Id,
                Source = new CardSource { Kind = CardSourceKind.Plugin, Plugin = manifest.Id, Name = manifest.Name },
                Actions = actions,
                UpdatedAt = now,
                StaleAfterS = manifest.Interval == "manual" ? null : (int)Math.Min(int.MaxValue, DurationText.Parse(manifest.Interval).TotalSeconds * 3),
                Link = null,
            });
        }

        return (cards, null);
    }

    private static string? CheckBlock(Block b, Dictionary<string, PluginActionDecl> declared)
    {
        if (b is TreeBlock)
        {
            return "Tree is not in the plugin block subset";
        }

        if (b is AlertBlock)
        {
            return "Alert is not in the plugin block subset (toasts go through rules)";
        }

        foreach (var v in Values(b))
        {
            if (v is { IsBinding: true })
            {
                return b.Kind + ": plugins send literal values only";
            }
        }

        switch (b)
        {
            case SparklineBlock s:
                if (s.Series is not null)
                {
                    return "Sparkline: plugins send points, not a series";
                }

                if ((s.Points?.Count ?? 0) > MaxSparklinePoints)
                {
                    return "Sparkline: more than 60 points";
                }

                break;
            case TableBlock t:
                if (t.Max is > MaxTableRows)
                {
                    return "Table: max is 12 rows";
                }

                if (t.Rows.AsElement() is { ValueKind: JsonValueKind.Array } rows && rows.GetArrayLength() > MaxTableRows)
                {
                    return "Table: more than 12 rows";
                }

                break;
            case ActionsBlock ab:
                foreach (var id in ab.Actions)
                {
                    if (!declared.ContainsKey(id))
                    {
                        return "Actions: " + id + " is not declared in plugin.json";
                    }
                }

                break;
        }

        return null;
    }

    private static IEnumerable<Value?> Values(Block b)
    {
        yield return b.Severity;
        switch (b)
        {
            case MetricBlock m:
                yield return m.Value;
                break;
            case BarBlock bar:
                yield return bar.Value;
                yield return bar.Max;
                yield return bar.Text;
                break;
            case GaugeBlock g:
                yield return g.Value;
                break;
            case TableBlock t:
                yield return t.Rows;
                break;
            case ListBlock l:
                yield return l.Items;
                break;
            case ProgressBlock p:
                yield return p.Pct;
                yield return p.Done;
                yield return p.Total;
                yield return p.Text;
                break;
            case BadgeBlock badge:
                yield return badge.Text;
                break;
            case CountdownBlock cd:
                yield return cd.Until;
                break;
        }
    }
}
