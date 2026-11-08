using System.Text;

namespace Tasktor.Core.Workflow;

/// <summary>
/// C5's title tokenizer (the Python reference in <c>tasktor_reference</c> is identical): the app-name suffix removed
/// ("… - Google Chrome"), redaction tokens removed, lower-cased, split on anything that is not a letter or digit,
/// stopwords and pure numbers dropped, 2–30 characters; plus <c>dom:&lt;host&gt;</c>.
/// </summary>
public static class Tokenizer
{
    public static readonly string[] Separators = [" - ", " — ", " – ", " | "];

    public static readonly HashSet<string> KnownSuffixes = new(StringComparer.Ordinal)
    {
        "google chrome", "microsoft edge", "mozilla firefox", "brave", "opera", "vivaldi", "visual studio code", "visual studio",
        "notepad", "file explorer", "word", "excel", "powerpoint", "microsoft word", "microsoft excel", "microsoft powerpoint",
        "windows terminal", "terminal", "obsidian", "notion", "slack", "discord", "spotify", "zoom", "outlook", "cursor",
    };

    public static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "new", "of", "on", "or", "tab",
        "the", "this", "that", "to", "untitled", "was", "with", "you", "your",
    };

    public static List<string> Tokens(string? title, string? app, string? domain)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var t in Words(StripSuffix(title, app)))
            {
                list.Add(t);
            }
        }

        if (!string.IsNullOrEmpty(domain))
        {
            list.Add("dom:" + domain.ToLowerInvariant());
        }

        return list;
    }

    public static string StripSuffix(string title, string? app)
    {
        foreach (var sep in Separators)
        {
            var i = title.LastIndexOf(sep, StringComparison.Ordinal);
            if (i <= 0)
            {
                continue;
            }

            var last = title[(i + sep.Length)..].Trim().ToLowerInvariant();
            var appBase = app is null ? "" : (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app).ToLowerInvariant();
            if (KnownSuffixes.Contains(last) || (appBase.Length >= 3 && last.Contains(appBase, StringComparison.Ordinal)))
            {
                return title[..i];
            }
        }

        return title;
    }

    private static IEnumerable<string> Words(string text)
    {
        // Redaction tokens (‹email› …) are removed whole, never split into words.
        var sb = new StringBuilder(text.Length);
        var inRedaction = false;
        foreach (var ch in text)
        {
            if (ch == '‹')
            {
                inRedaction = true;
                sb.Append(' ');
                continue;
            }

            if (ch == '›')
            {
                inRedaction = false;
                continue;
            }

            if (!inRedaction)
            {
                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }
        }

        foreach (var w in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length is >= 2 and <= 30 && !Stopwords.Contains(w) && !w.All(char.IsDigit))
            {
                yield return w;
            }
        }
    }
}
