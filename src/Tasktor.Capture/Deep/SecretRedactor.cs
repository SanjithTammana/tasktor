using System.Text.RegularExpressions;

namespace Tasktor.Capture.Deep;

/// <summary>
/// The T1 secret detectors (C spec 5.7), applied to every text before any vault write: card numbers (Luhn), IBANs
/// (mod-97), US SSNs (area check), API keys and tokens (known prefixes, JWTs), private-key blocks, high-entropy runs
/// (≥ 32 chars, ≥ 4.0 bits/char) and user regexes. A match becomes <c>⟦redacted:kind⟧</c>. Idempotent. Exclusion lists
/// are the real control; this is the second net.
/// </summary>
public sealed partial class SecretRedactor
{
    private readonly List<Regex> _user;

    public SecretRedactor(IEnumerable<string>? userPatterns = null) =>
        _user = [.. (userPatterns ?? []).Select(p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))];

    public static string Mark(string kind) => "⟦redacted:" + kind + "⟧";

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35})", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.CultureInvariant)]
    private static partial Regex Card();

    [GeneratedRegex(@"[A-Za-z0-9+/=_-]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex Run();

    /// <summary>Scrub <paramref name="text"/>; returns the text and hits per kind.</summary>
    public (string Text, Dictionary<string, int> Hits) Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        string Replace(string input, Regex re, string kind, Func<string, bool>? check = null) => re.Replace(input, m =>
        {
            if (check is not null && !check(m.Value))
            {
                return m.Value;
            }

            hits[kind] = hits.GetValueOrDefault(kind) + 1;
            return Mark(kind);
        });

        var t = Replace(text, PrivateKey(), "private_key");
        t = Replace(t, Jwt(), "jwt");
        t = Replace(t, ApiKey(), "api_key");
        t = Replace(t, Iban(), "iban", IbanValid);
        t = Replace(t, Ssn(), "ssn", SsnValid);
        t = Replace(t, Card(), "card", Luhn);
        t = Replace(t, Run(), "token", HighEntropy);
        foreach (var u in _user)
        {
            t = Replace(t, u, "user_rule");
        }

        return (t, hits);
    }

    public static bool Luhn(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    public static bool IbanValid(string candidate)
    {
        var s = candidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (s.Length is < 15 or > 34)
        {
            return false;
        }

        var rearranged = s[4..] + s[..4];
        var rem = 0;
        foreach (var ch in rearranged)
        {
            var v = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetterUpper(ch) ? ch - 'A' + 10 : -1;
            if (v < 0)
            {
                return false;
            }

            rem = v >= 10 ? ((rem * 100) + v) % 97 : ((rem * 10) + v) % 97;
        }

        return rem == 1;
    }

    public static bool SsnValid(string candidate)
    {
        var area = int.Parse(candidate.AsSpan(0, 3), System.Globalization.CultureInfo.InvariantCulture);
        return area is not 0 and not 666 and < 900 && candidate.Substring(4, 2) != "00" && candidate.Substring(7, 4) != "0000";
    }

    /// <summary>Shannon entropy ≥ 4.0 bits per char and a mix of character classes (rules out long words and hex ids).</summary>
    public static bool HighEntropy(string candidate)
    {
        if (candidate.Contains('⟦', StringComparison.Ordinal))
        {
            return false;
        }

        var counts = new Dictionary<char, int>();
        foreach (var c in candidate)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        var h = 0.0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / candidate.Length;
            h -= p * Math.Log2(p);
        }

        var classes = (candidate.Any(char.IsAsciiLetterUpper) ? 1 : 0) + (candidate.Any(char.IsAsciiLetterLower) ? 1 : 0) + (candidate.Any(char.IsAsciiDigit) ? 1 : 0);
        return h >= 4.0 && classes >= 2;
    }
}
