using Tasktor.Capture.Deep;
using Tasktor.Capture.UIAutomation;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>#8: name / automation-id caps and text bodies (C18.3 terminals, documents) through visible ranges.</summary>
public sealed class UiaWalkTextTests
{
    [Fact]
    public void Names_and_automation_ids_are_capped()
    {
        var root = new Node(new string('n', 5000)) { Id = new string('i', 5000) };
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(UiaWalkLogic.MaxNameChars, walk.Text(walk.Lines[0].NameId)!.Length);
        Assert.Equal(UiaWalkLogic.MaxNameChars, UiaWalkLogic.Cap(new string('x', 5000), UiaWalkLogic.MaxNameChars)!.Length);
        Assert.Equal(UiaWalkLogic.MaxAutomationIdChars, UiaWalkLogic.Cap(new string('x', 5000), UiaWalkLogic.MaxAutomationIdChars)!.Length);
        Assert.Equal(1, UiaWalkLogic.Cap("a😀", 2)!.Length); // never splits a surrogate pair
    }

    [Fact]
    public void A_text_body_adds_its_visible_lines_with_stable_refs()
    {
        var root = new Node("Terminal") { Lines = ["PS> build", "ok", "", "PS> test"] };
        var first = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(["Terminal", "PS> build", "ok", "PS> test"], first.Lines.Select(l => first.Text(l.NameId)));
        Assert.All(first.Lines.Skip(1), l => Assert.Equal(UiaWalkLogic.TextLineRole, l.Role));
        Assert.Equal(1, root.TextLimit > 0 ? 1 : 0);

        root.Lines = ["ok", "", "PS> test", "PS> next"]; // scrolled by one line
        var second = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        var common = first.Lines.Select(l => l.Ref).Intersect(second.Lines.Select(l => l.Ref)).Count();
        Assert.Equal(3, common); // the root, "ok" and "PS> test" keep their refs
    }

    [Fact]
    public void A_password_or_unknown_body_never_has_its_text_requested()
    {
        var root = new Node("root");
        var password = new Node("box") { Password = true, Lines = ["secret"] };
        var unknown = new Node("box2") { Password = null, Lines = ["secret"] };
        root.Kids.AddRange([password, unknown]);
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(0, password.TextLimit + unknown.TextLimit);
        Assert.DoesNotContain("secret", walk.Strings);
    }

    [Fact]
    public void Text_bodies_are_capped_in_bytes()
    {
        var root = new Node("doc") { Lines = [.. Enumerable.Range(0, 1000).Select(i => "line " + i + new string('x', 40))] };
        var walk = UiaWalkLogic.TryWalk(new ViewKey(1, 2), root)!.Value.Reveal();
        Assert.Equal(UiaWalkLogic.MaxTextBytes, root.TextLimit);
        Assert.True(walk.Lines.Skip(1).Sum(l => System.Text.Encoding.UTF8.GetByteCount(walk.Text(l.NameId)!) + 1) <= UiaWalkLogic.MaxTextBytes);
    }

    private sealed class Node(string name) : IUiaWalkNode
    {
        public UiaNodeKind Kind => UiaNodeKind.Content;
        public byte Role => 30;
        public bool? Offscreen => false;
        public bool? Password { get; set; } = false;
        public string? Id { get; set; }
        public IReadOnlyList<string>? Lines { get; set; }
        public int TextLimit { get; private set; }
        public List<IUiaWalkNode> Kids { get; } = [];
        public Sensitive<string>? Name => Sensitive.Of(name);
        public Sensitive<string>? AutomationId => Id is null ? null : Sensitive.Of(Id);
        public Sensitive<string>? ReadValue(int maxUtf8Bytes) => null;
        public Sensitive<string>? ReadVisibleText(int maxUtf8Bytes)
        {
            TextLimit = maxUtf8Bytes;
            return Lines is null ? null : Sensitive.Of(string.Join('\n', Lines));
        }

        public IReadOnlyList<IUiaWalkNode> Children => Kids;
    }
}

public sealed class UiaRolesTests
{
    [Fact]
    public void Control_types_map_to_roles_kinds_and_targets()
    {
        Assert.Equal((byte)0, UiaRoles.FromControlType(50000));
        Assert.Null(UiaRoles.FromControlType(50041));
        Assert.Null(UiaRoles.FromControlType(0));
        Assert.Equal("button: Save", UiaRoles.Target(0, "Save"));
        Assert.Equal("edit", UiaRoles.Target(UiaRoles.Edit, " "));
        Assert.Equal(UiaNodeKind.Decorative, UiaRoles.Kind(UiaRoles.ScrollBar, "Vertical"));
        Assert.Equal(UiaNodeKind.Decorative, UiaRoles.Kind(UiaRoles.Image, null));
        Assert.Equal(UiaNodeKind.Content, UiaRoles.Kind(UiaRoles.Image, "Chart of sales"));
        Assert.Equal(UiaNodeKind.Unknown, UiaRoles.Kind(null, "x"));
        Assert.Equal("app bar", UiaRoles.Name(40));
    }
}
