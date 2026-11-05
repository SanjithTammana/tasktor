using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Loop;
using Tasktor.Core.State;
using Tasktor.Tests.Shared;
using SnapshotRecord = Tasktor.Contracts.Snapshot.Snapshot;

namespace Tasktor.Core.Tests;

internal sealed class FixedCards(params Card[] cards) : ICardProvider
{
    public List<Card> Items { get; } = [.. cards];

    public IReadOnlyList<Card> Cards() => Items;
}

public sealed class SnapshotTests
{
    private static SnapshotRecord Fixture() =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "tasktor.snapshot-1", "full.json")), ContractsJsonContext.Default.Snapshot)!;

    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public LogicLoop Loop { get; }
        public StateTracker State { get; } = new();
        public CardTemplates Cards { get; } = new();
        public FixedCards Provider { get; } = new();
        public List<byte[]> Files { get; } = [];
        public SnapshotPublisher Publisher { get; }
        public AgentInfo Agent { get; set; } = new() { Version = "0.1.0" };

        public Rig(long seqStart = 0)
        {
            Loop = new LogicLoop(Clock);
            Cards.Add(Provider);
            Publisher = new SnapshotPublisher(Loop, State, Cards, () => Agent, Files.Add, seqStart);
        }
    }

    [Fact]
    public void Snapshot_json_matches_the_fixture()
    {
        var fx = Fixture();
        var rig = new Rig(seqStart: fx.Seq - 1) { Agent = fx.Agent };
        rig.Clock.UtcNow = fx.GeneratedAt;
        rig.State.Update(_ => fx.State);
        rig.Provider.Items.AddRange(fx.Cards!);
        Assert.True(rig.Publisher.Publish());
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "tasktor.snapshot-1", "full.json")));
        var actual = JsonNode.Parse(Assert.Single(rig.Files));
        Assert.True(JsonNode.DeepEquals(expected, actual), actual!.ToJsonString());
    }

    [Fact]
    public void Every_binding_in_the_published_cards_resolves()
    {
        var fx = Fixture();
        var root = JsonSerializer.SerializeToNode(fx, ContractsJsonContext.Default.Snapshot)!;
        var bindings = new List<string>();
        foreach (var card in fx.Cards!)
        {
            Collect(card.Severity, bindings);
            foreach (var b in card.Blocks)
            {
                Collect(b.Severity, bindings);
                switch (b)
                {
                    case MetricBlock m: Collect(m.Value, bindings); break;
                    case BadgeBlock g: Collect(g.Text, bindings); break;
                    case TableBlock t: Collect(t.Rows, bindings); break;
                }
            }
        }

        Assert.NotEmpty(bindings);
        foreach (var p in bindings)
        {
            Assert.True(JsonPointer.TryResolve(root, p, out _), "unresolved " + p);
        }
    }

    private static void Collect(Value? v, List<string> into)
    {
        if (v?.Bind is { } p)
        {
            into.Add(p);
        }
    }

    [Fact]
    public void Publishes_only_on_change()
    {
        var rig = new Rig();
        Assert.True(rig.Publisher.Publish());
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(rig.Publisher.Publish()); // nothing changed
        rig.State.Update(s => s with { System = new SystemState { CpuPct = 10 } });
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(rig.Publisher.Publish());
        rig.State.Update(s => s with { System = new SystemState { CpuPct = 10 } }); // same value, new object
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(rig.Publisher.Publish());
        Assert.Equal(2, rig.Files.Count);
    }

    [Fact]
    public void Writes_at_most_once_per_second_while_values_change()
    {
        var rig = new Rig();
        for (var i = 0; i < 100; i++)
        {
            rig.State.Update(s => s with { System = new SystemState { CpuPct = i } });
            rig.Publisher.MarkChanged();
            rig.Loop.RunDue();
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100)); // 10 s in all
            rig.Loop.RunDue();
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Loop.RunDue();
        Assert.InRange(rig.Files.Count, 9, 11);
        var last = JsonNode.Parse(rig.Files[^1])!;
        Assert.Equal(99, last["state"]!["system"]!["cpu_pct"]!.GetValue<double>()); // the newest value always lands
    }

    [Fact]
    public void Idle_agent_schedules_nothing()
    {
        var rig = new Rig();
        rig.Publisher.Publish();
        Assert.Null(rig.Loop.NextDue);
    }

    [Fact]
    public void Card_structure_changes_republish_and_report()
    {
        var rig = new Rig();
        var seen = new List<SnapshotPublished>();
        rig.Publisher.Published += seen.Add;
        rig.Publisher.Publish();
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Provider.Items.Add(new Card { Id = "x", Title = "X", Blocks = [new BadgeBlock { Text = Value.Literal("hi") }] });
        Assert.True(rig.Publisher.Publish());
        Assert.True(seen[^1].CardsChanged);
        Assert.Empty(seen[^1].StateOps);
    }
}

public sealed class StateDeltaTests
{
    [Fact]
    public void One_changed_value_gives_one_replace()
    {
        var t = new StateTracker();
        t.Update(s => s with { System = new SystemState { CpuPct = 34, RamUsedPct = 71 } });
        t.TakeDelta();
        t.Update(s => s with { System = s.System! with { CpuPct = 40 } });
        var op = Assert.Single(t.TakeDelta());
        Assert.Equal("replace", op.Op);
        Assert.Equal("/state/system/cpu_pct", op.Path);
        Assert.Empty(t.TakeDelta());
    }

    [Fact]
    public void Applying_the_ops_reproduces_the_new_state()
    {
        var rng = new Random(3);
        var t = new StateTracker();
        JsonNode mirror = new JsonObject { ["state"] = new JsonObject() };
        for (var round = 0; round < 40; round++)
        {
            var apps = Enumerable.Range(0, rng.Next(0, 5))
                .Select(i => new AppRow { AppId = "a" + i, Name = "A" + i, CpuPct = rng.Next(100), Count = rng.Next(1, 4) })
                .ToList();
            t.Update(s => s with
            {
                System = rng.Next(3) == 0 ? null : new SystemState { CpuPct = rng.Next(100), Verdict = rng.Next(2) == 0 ? null : "v" },
                Apps = apps,
                Fullscreen = rng.Next(2) == 0 ? Fullscreen.None : null,
            });
            foreach (var op in t.TakeDelta())
            {
                mirror = JsonPointer.Apply(mirror, op.Op, op.Path, op.Value)!;
            }

            Assert.True(JsonNode.DeepEquals(t.ToJson(), mirror["state"]), "round " + round);
        }
    }
}

public sealed class GlanceTextTests
{
    [Fact]
    public void Top_metrics_with_severity_word_and_job_progress()
    {
        var fx = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "tasktor.snapshot-1", "full.json")), ContractsJsonContext.Default.Snapshot)!;
        var state = JsonSerializer.SerializeToNode(fx.State, ContractsJsonContext.Default.SnapshotState)!.AsObject();
        var text = GlanceText.Compose(fx.Cards!, state);
        Assert.Equal("Warning: CPU 34% · RAM 71% · train.py 42%", text);
    }

    [Fact]
    public void Never_longer_than_127()
    {
        var card = new Card
        {
            Id = "core.system",
            Title = "S",
            Blocks = [new MetricBlock { Label = new string('x', 200), Value = Value.Literal(1) }],
        };
        Assert.Equal(127, GlanceText.Compose([card], []).Length);
    }

    [Fact]
    public void Missing_binding_renders_a_dash()
    {
        var card = new Card { Id = "core.system", Title = "S", Blocks = [new MetricBlock { Label = "CPU", Value = Value.Binding("/state/nope"), Unit = "%" }] };
        Assert.Equal("CPU —%", GlanceText.Compose([card], []));
    }
}
