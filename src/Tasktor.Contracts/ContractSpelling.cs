using System.Text.Json.Serialization.Metadata;

namespace Tasktor.Contracts;

/// <summary>
/// "Did you mean …?" for contracts that refuse unknown names (plugin contract 2). The name is still refused; this only
/// words the refusal so a typo is quick to fix.
/// </summary>
public static class ContractSpelling
{
    /// <summary>The JSON names of a record's fields, as the context writes them.</summary>
    public static IReadOnlyList<string> FieldNames(JsonTypeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.Properties.Select(p => p.Name).ToList();
    }

    /// <summary>
    /// The one known name closest to <paramref name="unknown"/>: at most 2 edits (a swap of two neighbours is one) and
    /// fewer edits than half its length, ignoring case and treating <c>-</c> as <c>_</c>. Null when nothing is that close
    /// or two names tie.
    /// </summary>
    public static string? Closest(string unknown, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(unknown);
        ArgumentNullException.ThrowIfNull(known);
        var u = Fold(unknown);
        string? best = null;
        var bestDistance = int.MaxValue;
        var tie = false;
        foreach (var name in known)
        {
            var d = Distance(u, Fold(name));
            if (d < bestDistance)
            {
                (best, bestDistance, tie) = (name, d, false);
            }
            else if (d == bestDistance && name != best)
            {
                tie = true;
            }
        }

        return best is not null && !tie && bestDistance <= 2 && bestDistance * 2 < u.Length ? best : null;
    }

    /// <summary>"Unknown field 'capabilites'. Did you mean 'capabilities'?", or without the question when nothing is close.</summary>
    public static string Refusal(string what, string unknown, IEnumerable<string> known)
    {
        var close = Closest(unknown, known);
        var head = $"Unknown {what} '{unknown}'.";
        return close is null ? head : $"{head} Did you mean '{close}'?";
    }

    private static string Fold(string s) => s.ToLowerInvariant().Replace('-', '_');

    // Optimal string alignment distance: insert, delete, substitute, and swap two neighbours, each costing 1.
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                }

                d[i, j] = v;
            }
        }

        return d[a.Length, b.Length];
    }
}
