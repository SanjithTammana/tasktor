using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Folders;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 I12: settled new files raise file.created; the rule's run command starts only from a clicked toast.</summary>
public sealed class FolderActionTests : IDisposable
{
    private readonly FeatureRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private sealed class FakeFolders : IEventSource<FileEvent>
    {
        public Action<FileEvent>? Post { get; private set; }

        public string Name => "fake-folders";

        public void Start(Action<FileEvent> post) => Post = post;

        public void Stop() => Post = null;
    }

    [Fact]
    public void A_file_settles_after_2_stable_seconds_unlocked_and_partial_downloads_never_fire()
    {
        var settle = new FileSettle();
        var t0 = DateTimeOffset.UnixEpoch;
        var files = new Dictionary<string, (long, bool)> { ["/d/a.pdf"] = (10, false), ["/d/b.pdf"] = (5, true) };
        settle.Seen("/d", "/d/a.pdf", t0);
        settle.Seen("/d", "/d/b.pdf", t0);
        settle.Seen("/d", "/d/c.pdf.crdownload", t0);
        settle.Seen("/d", "/d/~$doc.docx", t0);
        Assert.Equal(2, settle.Pending);
        (long, bool)? Probe(string p) => files.TryGetValue(p, out var f) ? f : null;
        Assert.Empty(settle.Check(t0.AddSeconds(1), Probe));
        files["/d/a.pdf"] = (20, false); // still growing
        Assert.Empty(settle.Check(t0.AddSeconds(2), Probe));
        Assert.Empty(settle.Check(t0.AddSeconds(3), Probe));
        Assert.Equal([("/d", "/d/a.pdf")], settle.Check(t0.AddSeconds(4), Probe));
        Assert.Empty(settle.Check(t0.AddSeconds(10), Probe)); // b is locked
        files["/d/b.pdf"] = (5, false);
        Assert.Single(settle.Check(t0.AddSeconds(11), Probe));
    }

    [Fact]
    public void The_real_watch_reports_a_finished_file_once_and_never_its_partial_name()
    {
        var dir = Path.Combine(_rig.Store.Root, "drop");
        Directory.CreateDirectory(dir);
        var seen = new List<FileEvent>();
        using var source = new FolderWatchSource([dir], () => DateTimeOffset.UtcNow);
        source.Start(e =>
        {
            lock (seen)
            {
                seen.Add(e);
            }
        });
        File.WriteAllText(Path.Combine(dir, "report.pdf.part"), "x");
        File.Move(Path.Combine(dir, "report.pdf.part"), Path.Combine(dir, "report.pdf"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
            {
                if (seen.Count > 0)
                {
                    break;
                }
            }

            Thread.Sleep(100);
        }

        Thread.Sleep(1500);
        lock (seen)
        {
            var e = Assert.Single(seen);
            Assert.EndsWith("report.pdf", e.Path.Reveal(), StringComparison.Ordinal);
            Assert.Equal(dir, e.Folder);
        }
    }

    [Fact]
    public void The_new_pdf_rule_offers_run_and_a_click_starts_exactly_the_declared_argv()
    {
        var downloads = Path.Combine(_rig.Store.Root, "Downloads");
        var engineFolder = (string n) => n == "Downloads" ? downloads : null;
        var rules = RulesLoader.Load(null, """
            { "rules": [ { "id": "new-pdf", "when": { "event": "file.created", "folder": "{Downloads}", "glob": "*.pdf" },
                "then": [ { "toast": "New PDF: {file}", "buttons": ["run:convert"] } ],
                "commands": { "convert": ["{UserProfile}/tools/convert.exe", "{path}"] } } ] }
            """);
        Assert.Empty(rules.Errors);
        Assert.Equal([downloads], FolderAction.FoldersOf(rules, engineFolder));

        var runner = new RuleRunner(_rig.Loop, new RuleEngine(n => n switch { "Downloads" => downloads, "UserProfile" => "/home/u", _ => null }), _rig.Router, _rig.Notifier, _rig.Log, () => _rig.Features, c => _rig.State.Update(c));
        runner.SetRules(rules);
        var started = new List<IReadOnlyList<string>>();
        runner.RunCommand = argv =>
        {
            started.Add(argv);
            return true;
        };
        var fake = new FakeFolders();
        var feature = _rig.Add(new FolderAction(_rig.Context, _ => fake) { Folders = () => FolderAction.FoldersOf(rules, engineFolder) });
        Assert.Equal([downloads], feature.Watched);

        fake.Post!(new FileEvent(downloads, Sensitive.Of(Path.Combine(downloads, "notes.txt")), _rig.Clock.UtcNow));
        fake.Post!(new FileEvent(downloads, Sensitive.Of(Path.Combine(downloads, "invoice.pdf")), _rig.Clock.UtcNow));
        _rig.Loop.RunPending();
        var toast = Assert.Single(runner.Evaluate(), o => o.Toast is not null).Toast!;
        Assert.Equal("New PDF: invoice.pdf", toast.Title);
        var run = Assert.Single(toast.Buttons).Request;
        Assert.Empty(started); // nothing runs unclicked

        var forged = _rig.Act(run with { Args = new Dictionary<string, string> { ["path"] = "/etc/passwd" } });
        Assert.Equal(ActionStatus.Refused, forged.Status);
        var clicked = _rig.Act(run);
        Assert.Equal(ActionStatus.Ok, clicked.Status);
        Assert.Equal(["/home/u/tools/convert.exe", Path.Combine(downloads, "invoice.pdf")], Assert.Single(started));
        Assert.DoesNotContain(_rig.LogLines.Lines, l => l.Contains("invoice", StringComparison.Ordinal));

        var fromRule = _rig.Act(run with { Origin = Origin.Rule, Rule = "new-pdf" });
        Assert.Equal(ActionStatus.Refused, fromRule.Status); // rules never run anything
    }
}
