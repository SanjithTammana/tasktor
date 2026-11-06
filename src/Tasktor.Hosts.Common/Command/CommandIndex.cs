using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Command;

/// <summary>One result: an action to send through <c>act</c> (origin <c>command</c>), or a deep-window page to open.</summary>
public sealed record CommandItem(string Label, CardAction? Action = null, string? Page = null);

/// <summary>A heading and its results ("Chrome", "Suggested", "Jobs", "Pages").</summary>
public sealed record CommandSection(string Heading, IReadOnlyList<CommandItem> Items);

/// <summary>What the command line shows for a query: the current verdict, then the sections. Enter runs <see cref="First"/>.</summary>
public sealed record CommandView(string Verdict, IReadOnlyList<CommandSection> Sections)
{
    public CommandItem? First => Sections.SelectMany(s => s.Items).FirstOrDefault();
}

/// <summary>
/// U4 amended D-G12: the command line's results. An empty query shows the verdict and the sentence's own actions. A
/// query matches apps (their verbs: priority, efficiency mode, end, don't capture), card actions the shelf could run,
/// jobs and the deep window's pages; apps first (the noun the user typed), then suggestions, jobs, pages. Matching is
/// case-insensitive: a prefix beats a word start beats a substring beats letters in order. Activity search (C21) joins
/// as its own section once the content index exists.
/// </summary>
public static class CommandIndex
{
    public const int PerSection = 4;
    public const int MaxApps = 2;

    /// <summary>D-G13's pages, in the deep window's order.</summary>
    public static IReadOnlyList<(string Id, string Name)> Pages { get; } =
    [
        ("home", "Home"), ("system", "System"), ("jobs", "Jobs"), ("activity", "Activity"), ("learned", "What Tasktor learned"),
        ("focus", "Focus"), ("windows", "Windows"), ("data", "Your data"), ("settings", "Settings"),
    ];

    public static CommandView Query(string? query, IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(resolver);
        var sentence = ShelfComposer.Compose(cards, resolver);
        var q = (query ?? string.Empty).Trim();
        var sections = new List<CommandSection>();
        if (q.Length == 0)
        {
            var own = sentence.Nouns.SelectMany(n => n.Actions).DistinctBy(a => (a.Action, Key(a))).Take(PerSection).Select(a => new CommandItem(a.Label, a)).ToList();
            Add(sections, "Suggested", own);
            return new CommandView(sentence.Text, sections);
        }

        foreach (var app in Rows(resolver, "/state/apps")
                     .Select(r => (Id: Str(r, "app_id"), Name: Str(r, "name")))
                     .Where(a => a.Id is not null && a.Name is not null)
                     .Select(a => (a.Id, a.Name, Score: Score(q, a.Name!)))
                     .Where(a => a.Score > 0)
                     .OrderByDescending(a => a.Score)
                     .Take(MaxApps))
        {
            Add(sections, app.Name!, AppVerbs(app.Id!, app.Name!));
        }

        var suggested = cards
            .SelectMany(c => (c.Actions ?? []).Where(ShelfComposer.ShelfRunnable).Select(a => (a, Score: Math.Max(Score(q, a.Label), Math.Max(Score(q, a.Noun ?? string.Empty), Score(q, c.Title) * 0.9)))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .DistinctBy(x => (x.a.Action, Key(x.a)))
            .Take(PerSection)
            .Select(x => new CommandItem(x.a.Label, x.a))
            .ToList();
        Add(sections, "Suggested", suggested);

        var jobs = Rows(resolver, "/state/jobs")
            .Select(r => (Id: Str(r, "id"), Label: Str(r, "label"), Status: Str(r, "status")))
            .Where(j => j.Id is not null && j.Label is not null && Score(q, j.Label!) > 0)
            .OrderByDescending(j => Score(q, j.Label!))
            .SelectMany(j => JobVerbs(j.Id!, j.Label!, j.Status))
            .Take(PerSection)
            .ToList();
        Add(sections, "Jobs", jobs);

        var pages = Pages.Where(p => Score(q, p.Name) > 0).OrderByDescending(p => Score(q, p.Name)).Take(PerSection).Select(p => new CommandItem(p.Name, Page: p.Id)).ToList();
        Add(sections, "Pages", pages);
        return new CommandView(sentence.Text, sections);
    }

    /// <summary>3 prefix, 2 a word starts with it, 1 substring, 0.5 its letters in order, 0 no match.</summary>
    public static double Score(string query, string text)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);
        if (query.Length == 0 || text.Length == 0)
        {
            return 0;
        }

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at > 0)
        {
            return char.IsLetterOrDigit(text[at - 1]) ? 1 : 2;
        }

        var i = 0;
        foreach (var ch in text)
        {
            if (i < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[i]))
            {
                i++;
            }
        }

        return i == query.Length ? 0.5 : 0;
    }

    private static List<CommandItem> AppVerbs(string id, string name)
    {
        var target = new Dictionary<string, string> { ["app"] = id };
        return
        [
            new($"Lower {name}'s priority", new CardAction { Id = "priority", Label = $"Lower {name}'s priority", Action = "priority.set", Target = target, Args = new Dictionary<string, string> { ["priority"] = "below_normal" } }),
            new($"Turn on efficiency mode for {name}", new CardAction { Id = "efficiency", Label = $"Turn on efficiency mode for {name}", Action = "efficiency.set", Target = target }),
            new($"End {name}…", new CardAction { Id = "end", Label = $"End {name}", Action = "app.end", Target = target, Destructive = true }),
            new($"Don't capture {name}", new CardAction { Id = "exclude", Label = $"Don't capture {name}", Action = "capture.exclude", Target = target }),
        ];
    }

    private static IEnumerable<CommandItem> JobVerbs(string id, string label, string? status)
    {
        var target = new Dictionary<string, string> { ["job"] = id };
        if (status is "done" or "failed")
        {
            yield break;
        }

        yield return status == "paused"
            ? new CommandItem($"Resume {label}", new CardAction { Id = "resume", Label = $"Resume {label}", Action = "job.resume", Target = target })
            : new CommandItem($"Pause {label}", new CardAction { Id = "pause", Label = $"Pause {label}", Action = "job.pause", Target = target });
        yield return new CommandItem($"End {label}…", new CardAction { Id = "end", Label = $"End {label}", Action = "job.end", Target = target, Destructive = true });
    }

    private static void Add(List<CommandSection> sections, string heading, List<CommandItem> items)
    {
        if (items.Count > 0)
        {
            sections.Add(new CommandSection(heading, items.Take(PerSection).ToList()));
        }
    }

    private static IEnumerable<JsonObject> Rows(BindingResolver resolver, string pointer) =>
        resolver.Resolve(Value.Binding(pointer)) is JsonArray rows ? rows.OfType<JsonObject>() : [];

    private static string? Str(JsonObject row, string key) =>
        row[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string Key(CardAction a) =>
        a.Target is null ? string.Empty : string.Join(",", a.Target.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
}
