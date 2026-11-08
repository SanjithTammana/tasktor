using Tasktor.Capture.Deep;
using Tasktor.Capture.UIAutomation;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

public sealed class UiaWalkLogicTests
{
    [Fact]
    public void Password_or_unknown_password_never_reads_values()
    {
        var root = new Node("root");
        var password = new Node("secret") { Password = true, Value = "fixture" };
        var unknown = new Node("unknown") { Password = null, Value = "fixture" };
        var normal = new Node("normal") { Password = false, Value = "safe" };
        root.Kids.AddRange([password, unknown, normal]);
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(0, password.Reads);
        Assert.Equal(0, unknown.Reads);
        Assert.Equal(1, normal.Reads);
        Assert.Contains(walk.Lines, line => walk.Text(line.ValueId) == "safe");
    }

    [Fact]
    public void Offscreen_unknown_and_unknown_visibility_skip_their_subtrees()
    {
        var root = new Node("root");
        var hidden = new Node("hidden") { Offscreen = true };
        var unknownVisibility = new Node("maybe") { Offscreen = null };
        var unknownRole = new Node("unknown") { Kind = UiaNodeKind.Unknown };
        hidden.Kids.Add(new Node("child"));
        unknownVisibility.Kids.Add(new Node("child"));
        unknownRole.Kids.Add(new Node("child"));
        root.Kids.AddRange([hidden, unknownVisibility, unknownRole]);
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Single(walk.Lines);
        Assert.Equal(0, hidden.ChildReads + unknownVisibility.ChildReads + unknownRole.ChildReads);
    }

    [Fact]
    public void Byte_limit_is_utf8_and_oversized_values_are_not_emitted()
    {
        var root = new Node("root") { Value = new string('\u00e9', 1025) };
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(-1, Assert.Single(walk.Lines).ValueId);
        Assert.Equal(2048, root.RequestedLimit);
    }

    [Fact]
    public void Limits_depth_and_total_visited_nodes()
    {
        var root = new Node("root");
        var current = root;
        for (var i = 0; i < 20; i++) { var child = new Node("depth"); current.Kids.Add(child); current = child; }
        for (var i = 0; i < 500; i++) root.Kids.Add(new Node("item"));
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(400, walk.Lines.Count);
        Assert.All(walk.Lines, line => Assert.InRange(line.Depth, (byte)0, (byte)12));
    }

    [Fact]
    public void Unrelated_sibling_does_not_change_existing_ref()
    {
        var root = new Node("root");
        root.Kids.Add(new Node("same"));
        var first = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        root.Kids.Insert(0, new Node("other"));
        var second = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(first.Lines[1].Ref, second.Lines[2].Ref);
    }

    [Fact]
    public void Source_failure_returns_no_partial_walk()
    {
        var root = new Node("root") { ThrowOnChildren = true };
        Assert.Null(UiaWalkLogic.TryWalk(new ViewKey(1, 2), root));
    }

    private sealed class Node(string name) : IUiaWalkNode
    {
        public UiaNodeKind Kind { get; set; } = UiaNodeKind.Content;
        public byte Role => 1;
        public bool? Offscreen { get; set; } = false;
        public bool? Password { get; set; } = false;
        public string? Value { get; set; }
        public bool ThrowOnChildren { get; set; }
        public int Reads { get; private set; }
        public int RequestedLimit { get; private set; }
        public int ChildReads { get; private set; }
        public List<IUiaWalkNode> Kids { get; } = [];
        public Sensitive<string>? Name => Sensitive.Of(name);
        public Sensitive<string>? AutomationId => null;
        public Sensitive<string>? ReadValue(int maxUtf8Bytes) { Reads++; RequestedLimit = maxUtf8Bytes; return Value is null ? null : Sensitive.Of(Value); }
        public IReadOnlyList<IUiaWalkNode> Children { get { ChildReads++; if (ThrowOnChildren) throw new InvalidOperationException(); return Kids; } }
    }
}
