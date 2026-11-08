using System.Collections.Concurrent;
using Tasktor.Capture.Deep;
using Tasktor.Capture.UIAutomation;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>#8: the UIA worker thread queues client events, walks off the caller's thread and resolves click targets.</summary>
public sealed class UiaWalkSourceTests
{
    [Fact]
    public void Events_from_any_thread_become_focus_and_walk_posts_within_three_seconds()
    {
        var client = new Client();
        var walks = new BlockingCollection<(UiaWalk Walk, string App, int Thread)>();
        var focus = new BlockingCollection<FocusTarget>();
        using var source = new UiaWalkSource(() => client, _ => "synthetic.exe", _ => false,
            (w, app) => walks.Add((w.Reveal(), app, Environment.CurrentManagedThreadId)), (t, _, _) => focus.Add(t));
        Assert.True(source.Start());

        var started = Environment.TickCount64;
        new Thread(() => client.RaiseFocus(new UiaFocusChange(5, new Leaf("field")))).Start();
        Assert.True(focus.TryTake(out var target, TimeSpan.FromSeconds(3)));
        Assert.Equal("synthetic.exe", target!.App);
        Assert.True(walks.TryTake(out var walk, TimeSpan.FromSeconds(3)));
        Assert.True(Environment.TickCount64 - started < 3000);
        Assert.Contains("Fixture text", walk.Walk.Strings);
        Assert.NotEqual(Environment.CurrentManagedThreadId, walk.Thread);
        Assert.Equal(client.CreatedOn, walk.Thread); // the client is used on its own thread only
    }

    [Fact]
    public async Task Click_targets_come_from_the_hit_test()
    {
        var client = new Client();
        using var source = new UiaWalkSource(() => client, _ => "synthetic.exe", _ => false, (_, _) => { }, (_, _, _) => { });
        Assert.Null(await source.ResolveAsync(1, 2, CancellationToken.None)); // not started: nothing
        Assert.True(source.Start());
        Assert.Equal("button: synthetic", (await source.ResolveAsync(1, 2, CancellationToken.None))?.Reveal());
    }

    [Fact]
    public void No_client_means_no_source()
    {
        using var source = new UiaWalkSource(() => null, _ => null, _ => false, (_, _) => { }, (_, _, _) => { });
        Assert.False(source.Start());
    }

    private sealed class Client : IUiaClient
    {
        public Client() => CreatedOn = Environment.CurrentManagedThreadId;

        public int CreatedOn { get; private set; }

        public event Action<long>? WindowDirty;

        public event Action<UiaFocusChange>? FocusChanged;

        public void RaiseFocus(UiaFocusChange f) => FocusChanged?.Invoke(f);

        public void Watch(long window) => CreatedOn = Environment.CurrentManagedThreadId;

        public IUiaWalkNode? Root(long window, bool textBodies)
        {
            WindowDirty?.Invoke(0); // events during a walk only queue
            return new Leaf("Fixture text");
        }

        public string? HitTest(int x, int y) => "button: synthetic";

        public void Dispose()
        {
        }
    }

    private sealed class Leaf(string name) : IUiaWalkNode
    {
        public UiaNodeKind Kind => UiaNodeKind.Content;

        public byte Role => 20;

        public bool? Offscreen => false;

        public bool? Password => false;

        public Sensitive<string>? Name => Sensitive.Of(name);

        public Sensitive<string>? AutomationId => null;

        public Sensitive<string>? ReadValue(int maxUtf8Bytes) => null;

        public IReadOnlyList<IUiaWalkNode> Children => [];
    }
}
