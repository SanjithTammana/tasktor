using Tasktor.Capture.Deep;
using Tasktor.Capture.UIAutomation;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>#8: per-window debounce, token bucket and hung skip; focus with password safety; walks timed out are dropped.</summary>
public sealed class UiaWorkerTests
{
    [Fact]
    public void Dirty_windows_are_debounced_and_rate_limited()
    {
        var s = new UiaScheduler();
        s.Dirty(1, 0);
        s.Dirty(1, 500);
        Assert.Empty(s.Due(1000));
        Assert.Equal([1L], s.Due(1250));
        Assert.Null(s.NextDueMs);

        // a burst of 5 walks, then one a second
        var walks = 0;
        for (var i = 0; i < 20; i++)
        {
            s.Dirty(1, 5000);
            walks += s.Due(5000 + UiaScheduler.DebounceMs).Count;
        }

        Assert.Equal(5, walks); // the sixth waits for a token
        Assert.Equal(5000 + UiaScheduler.DebounceMs + UiaScheduler.RefillMs, s.NextDueMs);
        Assert.Equal([1L], s.Due(5000 + UiaScheduler.DebounceMs + UiaScheduler.RefillMs));
    }

    [Fact]
    public void A_steady_stream_still_walks_within_the_cap()
    {
        var s = new UiaScheduler();
        for (var t = 0L; t < UiaScheduler.MaxDelayMs; t += 100) s.Dirty(1, t);
        Assert.Equal(UiaScheduler.MaxDelayMs, s.NextDueMs);
        Assert.Equal([1L], s.Due(UiaScheduler.MaxDelayMs));
    }

    [Fact]
    public void A_hung_window_is_skipped_for_a_minute()
    {
        var s = new UiaScheduler();
        s.Hung(1, 0);
        s.Dirty(1, 10);
        Assert.Empty(s.Due(5000));
        s.Dirty(1, UiaScheduler.HungSkipMs + 1);
        Assert.Equal([1L], s.Due(UiaScheduler.HungSkipMs + 1 + UiaScheduler.DebounceMs));
    }

    [Fact]
    public void Focus_reads_values_only_for_non_password_fields_and_moves_the_watch()
    {
        var (worker, client, posts) = New();
        var edit = new FakeNode("Subject", password: false) { Value = "draft" };
        var secret = new FakeNode("Password", password: true) { Value = "hunter2" };
        var unknown = new FakeNode("Unknown", password: null) { Value = "hunter3" };

        worker.OnFocus(new UiaFocusChange(10, edit));
        worker.OnFocus(new UiaFocusChange(10, secret));
        worker.OnFocus(new UiaFocusChange(20, unknown));
        Assert.Equal(0, secret.Reads + unknown.Reads);
        Assert.Equal(2, edit.Reads); // on focus in, and its final value on focus out
        Assert.Equal([10L, 20L], client.Watched);
        Assert.Equal("draft", posts.Focus[0].Value);
        Assert.Equal("draft", posts.Focus[1].Previous);
        Assert.True(posts.Focus[1].Target.IsPassword);
        Assert.Null(posts.Focus[2].Target.IsPassword);
        Assert.All(posts.Focus.Skip(1), f => Assert.Null(f.Value));
        Assert.Equal("synthetic.exe", posts.Focus[0].Target.App);
    }

    [Fact]
    public void Blocked_apps_are_neither_read_nor_walked()
    {
        var (worker, client, posts) = New(blocked: app => app == "synthetic.exe");
        var edit = new FakeNode("Subject", password: false) { Value = "draft" };
        worker.OnFocus(new UiaFocusChange(10, edit));
        Assert.Equal(0, edit.Reads);
        Assert.Null(posts.Focus.Single().Value);
        worker.Step(5000);
        Assert.Equal(0, client.Walks);
        Assert.Empty(posts.Walks);
    }

    [Fact]
    public void A_walk_of_the_watched_window_is_posted_and_one_past_the_timeout_is_dropped()
    {
        var clock = new long[] { 0 };
        var (worker, client, posts) = New(now: () => clock[0]);
        client.Tree = new FakeNode("Inbox", password: false);
        worker.OnFocus(new UiaFocusChange(10, new FakeNode("field", password: false)));
        clock[0] = 1000;
        worker.Step(clock[0]);
        var walk = Assert.Single(posts.Walks).Walk;
        Assert.Contains("Inbox", walk.Strings);
        Assert.Equal(10, walk.View.Window);

        client.WalkCostMs = UiaWorker.TimeoutMs + 1;
        client.OnWalk = () => clock[0] += client.WalkCostMs;
        worker.OnDirty(10);
        worker.OnDirty(99); // not the watched window: ignored
        clock[0] += 5000;
        worker.Step(clock[0]);
        Assert.Single(posts.Walks); // abandoned
        Assert.Equal(1, worker.TimedOut);
        worker.OnDirty(10);
        clock[0] += 5000;
        worker.Step(clock[0]);
        Assert.Equal(2, client.Walks); // the window is skipped for a minute
    }

    private static (UiaWorker, FakeClient, Posts) New(Func<string, bool>? blocked = null, Func<long>? now = null)
    {
        var client = new FakeClient();
        var posts = new Posts();
        var worker = new UiaWorker(client, _ => "synthetic.exe", blocked ?? (_ => false), (w, app) => posts.Walks.Add((w.Reveal(), app)),
            (t, p, v) => posts.Focus.Add((t, p?.Reveal(), v?.Reveal())), now ?? (() => 0));
        return (worker, client, posts);
    }

    private sealed class Posts
    {
        public List<(UiaWalk Walk, string App)> Walks { get; } = [];

        public List<(FocusTarget Target, string? Previous, string? Value)> Focus { get; } = [];
    }

    private sealed class FakeClient : IUiaClient
    {
        public List<long> Watched { get; } = [];

        public IUiaWalkNode Tree { get; set; } = new FakeNode("root", password: false);

        public int Walks { get; private set; }

        public long WalkCostMs { get; set; }

        public Action? OnWalk { get; set; }

        public event Action<long>? WindowDirty { add { } remove { } }

        public event Action<UiaFocusChange>? FocusChanged { add { } remove { } }

        public void Watch(long window) => Watched.Add(window);

        public IUiaWalkNode? Root(long window, bool textBodies)
        {
            Walks++;
            OnWalk?.Invoke();
            return Tree;
        }

        public string? HitTest(int x, int y) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeNode(string name, bool? password) : IUiaWalkNode
    {
        public string? Value { get; init; }

        public int Reads { get; private set; }

        public UiaNodeKind Kind => UiaNodeKind.Content;

        public byte Role => 4;

        public bool? Offscreen => false;

        public bool? Password => password;

        public Sensitive<string>? Name => Sensitive.Of(name);

        public Sensitive<string>? AutomationId => null;

        public Sensitive<string>? ReadValue(int maxUtf8Bytes)
        {
            Reads++;
            return Value is null ? null : Sensitive.Of(Value);
        }

        public IReadOnlyList<IUiaWalkNode> Children => [];
    }
}

/// <summary>E11.3: Tasktor's own windows are never captured: every shipped exe is on the list, and UIA never reads them.</summary>
public sealed class OwnAppsTests
{
    [Fact]
    public void Every_executable_in_src_is_an_own_app()
    {
        var root = Tasktor.Tests.Shared.RepoPaths.Root;
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = System.Xml.Linq.XDocument.Load(csproj);
            var output = xml.Descendants("OutputType").FirstOrDefault()?.Value;
            if (output is not ("Exe" or "WinExe"))
            {
                continue;
            }

            var name = xml.Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(csproj);
            Assert.Contains(name + ".exe", Suppressor.OwnApps, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Uia_never_reads_or_walks_a_tasktor_window()
    {
        var walked = 0;
        var values = 0;
        var client = new OwnClient(() => walked++);
        var focus = new List<(FocusTarget, Sensitive<string>?, Sensitive<string>?)>();
        var worker = new UiaWorker(client, _ => "tasktor.flyout.exe", _ => false, (_, _) => { }, (t, p, v) => focus.Add((t, p, v)), () => 0);
        worker.OnFocus(new UiaFocusChange(3, new OwnNode(() => values++)));
        worker.Step(10_000);
        Assert.Equal(0, values);
        Assert.Equal(0, walked);
        Assert.Null(focus.Single().Item3);
    }

    private sealed class OwnClient(Action walk) : IUiaClient
    {
        public event Action<long>? WindowDirty { add { } remove { } }

        public event Action<UiaFocusChange>? FocusChanged { add { } remove { } }

        public void Watch(long window)
        {
        }

        public IUiaWalkNode? Root(long window, bool textBodies)
        {
            walk();
            return null;
        }

        public string? HitTest(int x, int y) => null;

        public void Dispose()
        {
        }
    }

    private sealed class OwnNode(Action read) : IUiaWalkNode
    {
        public UiaNodeKind Kind => UiaNodeKind.Content;

        public byte Role => 4;

        public bool? Offscreen => false;

        public bool? Password => false;

        public Sensitive<string>? Name => Sensitive.Of("Note");

        public Sensitive<string>? AutomationId => null;

        public Sensitive<string>? ReadValue(int maxUtf8Bytes)
        {
            read();
            return Sensitive.Of("my note text");
        }

        public IReadOnlyList<IUiaWalkNode> Children => [];
    }
}
