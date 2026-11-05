using Tasktor.Contracts.Workflow;

namespace Tasktor.Tests.Shared;

/// <summary>
/// Deterministic synthetic work (titles are invented, never real): 14 days of four workflows (coding, thesis writing, mail,
/// coursework) with breaks, lunch locks and weekends, as raw C1 events. Feeds the extract and model fixtures.
/// </summary>
internal static class SyntheticWork
{
    public static readonly DateTimeOffset Start = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero); // a Monday

    private static readonly (string App, string Title, string? Domain)[][] Workflows =
    [
        [("code.exe", "{0}.rs - engine - Visual Studio Code", null), ("windowsterminal.exe", "cargo test engine {0}", null), ("code.exe", "{0}_tests.rs - engine - Visual Studio Code", null)],
        [("winword.exe", "Chapter {0} methods draft - Word", null), ("chrome.exe", "Scholar search sampling {0} - Google Chrome", "scholar.example.org"), ("zotero.exe", "Library sampling citations", null)],
        [("chrome.exe", "Inbox mail triage - Google Chrome", "mail.example.com"), ("chrome.exe", "Calendar week planning - Google Chrome", "calendar.example.com")],
        [("chrome.exe", "Lecture {0} linear algebra notes - Google Chrome", "course.example.edu"), ("acrobat.exe", "linear algebra problem set {0}", null)],
    ];

    private static readonly string[] Files = ["parser", "lexer", "codegen", "optimizer"];

    public static IEnumerable<CaptureEvent> Days(int days = 14, int seed = 11)
    {
        var rng = new Random(seed);
        for (var d = 0; d < days; d++)
        {
            var dayStart = Start.AddDays(d);
            var weekend = dayStart.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var t = dayStart.AddHours(weekend ? 10 : 8.5).AddMinutes(rng.Next(0, 30));
            var end = dayStart.AddHours(weekend ? 13 : 17.5);
            var lunchDone = weekend;
            var win = 1;
            while (t < end)
            {
                if (!lunchDone && t.Hour >= 12)
                {
                    yield return Ev(CaptureKind.Lock, t);
                    t = t.AddMinutes(45);
                    yield return Ev(CaptureKind.Unlock, t);
                    lunchDone = true;
                }

                var wf = weekend ? (rng.Next(2) == 0 ? 3 : 2) : rng.Next(4);
                var steps = Workflows[wf];
                var block = TimeSpan.FromMinutes(rng.Next(20, 50));
                var blockEnd = t + block;
                var n = rng.Next(1, 5);
                while (t < blockEnd)
                {
                    var (app, title, domain) = steps[rng.Next(steps.Length)];
                    var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, title, wf == 0 ? Files[rng.Next(Files.Length)] : n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return Ev(CaptureKind.Foreground, t, app, text, win++, domain);
                    t = t.AddSeconds(rng.Next(150, 720));
                    if (rng.Next(10) == 0)
                    {
                        // a glance at mail or chat
                        yield return Ev(CaptureKind.Foreground, t, "slack.exe", "channel", win++);
                        t = t.AddSeconds(rng.Next(5, 25));
                    }
                }

                if (rng.Next(3) == 0)
                {
                    yield return Ev(CaptureKind.IdleStart, t, idle: IdleKind.Input);
                    t = t.AddMinutes(rng.Next(6, 15));
                    yield return Ev(CaptureKind.IdleEnd, t);
                }
            }

            yield return Ev(CaptureKind.Lock, t);
            yield return Ev(CaptureKind.Unlock, dayStart.AddHours(31));
        }
    }

    private static CaptureEvent Ev(CaptureKind kind, DateTimeOffset at, string? app = null, string? title = null, int? win = null, string? domain = null, IdleKind? idle = null) => new()
    {
        Kind = kind,
        TsUtcMs = at.ToUnixTimeMilliseconds(),
        MonoMs = (long)(at - Start).TotalMilliseconds,
        App = app,
        Title = title,
        Win = win,
        Domain = domain,
        Idle = idle,
    };
}
