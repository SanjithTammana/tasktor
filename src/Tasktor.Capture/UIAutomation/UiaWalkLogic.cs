using System.Text;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.UIAutomation;

/// <summary>Explicit source classification; an unrecognized role is never treated as content.</summary>
public enum UiaNodeKind : byte
{
    Unknown = 0,
    Content = 1,
    Decorative = 2,
}

/// <summary>A cached UIA node. The OS adapter must obtain and use nodes on its UIA apartment.</summary>
public interface IUiaWalkNode
{
    UiaNodeKind Kind { get; }
    byte Role { get; }
    bool? Offscreen { get; }
    bool? Password { get; }
    Sensitive<string>? Name { get; }
    Sensitive<string>? AutomationId { get; }
    /// <summary>Called only when Password is explicitly false; implementations must enforce the requested byte limit.</summary>
    Sensitive<string>? ReadValue(int maxUtf8Bytes);

    /// <summary>
    /// C18.3: a text body's visible text (<c>TextPattern.GetVisibleRanges</c>; terminals, documents), lines split by '\n'.
    /// Called only when Password is explicitly false; null when the node has no text body.
    /// </summary>
    Sensitive<string>? ReadVisibleText(int maxUtf8Bytes) => null;
    IReadOnlyList<IUiaWalkNode> Children { get; }
}

/// <summary>
/// Builds a bounded snapshot from an already cached source. This runs on the UIA worker, not the hook thread;
/// the native source is responsible for enforcing a transaction timeout around cache construction and reads.
/// A failed source returns no partial result. The walk and its raw string arena leave this class wrapped in Sensitive.
/// </summary>
public static class UiaWalkLogic
{
    public const int MaxDepth = 12;
    public const int MaxNodes = 400;
    public const int MaxValueBytes = 2048;

    /// <summary>A text body's visible text per read (D-C18: 8 KB).</summary>
    public const int MaxTextBytes = 8 * 1024;

    /// <summary>Names and automation ids are cut to these (a provider can return any length).</summary>
    public const int MaxNameChars = 1024;
    public const int MaxAutomationIdChars = 256;

    /// <summary>The role of a text body's visible line (outside UIA's control-type range, 0–40).</summary>
    public const byte TextLineRole = 0xF0;

    /// <summary>Cut to <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    public static string? Cap(string? value, int max)
    {
        if (value is null || value.Length <= max) return value;
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut];
    }

    public static Sensitive<UiaWalk>? TryWalk(ViewKey view, IUiaWalkNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            var state = new WalkState();
            state.Visit(root, 0, view.RootRef, new Dictionary<ulong, int>());
            return Sensitive.Of(new UiaWalk(view, state.Lines.ToArray(), state.Strings.ToArray()));
        }
        catch (Exception)
        {
            // Provider failures must not emit an incomplete or accidentally unsafe walk.
            return null;
        }
    }

    private sealed class WalkState
    {
        private int _visited;
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        public List<string> Strings { get; } = [];
        public List<SnapLine> Lines { get; } = [];

        public void Visit(IUiaWalkNode node, int depth, ulong parent, Dictionary<ulong, int> siblings)
        {
            if (_visited >= MaxNodes || depth > MaxDepth) return;
            _visited++;
            var kind = node.Kind;
            if (kind is not (UiaNodeKind.Content or UiaNodeKind.Decorative) || node.Offscreen != false) return;

            var role = node.Role;
            var name = Cap(node.Name?.Reveal(), MaxNameChars);
            var automationId = Cap(node.AutomationId?.Reveal(), MaxAutomationIdChars);
            var identity = RefMinter.Ref(0, automationId, role, name, 0);
            siblings.TryGetValue(identity, out var siblingIndex);
            siblings[identity] = siblingIndex + 1;
            var reference = RefMinter.Ref(parent, automationId, role, name, siblingIndex);

            if (kind == UiaNodeKind.Content)
            {
                var nameId = Intern(name);
                var valueId = -1;
                if (node.Password == false && node.ReadValue(MaxValueBytes) is { } wrapped)
                {
                    var value = wrapped.Reveal();
                    if (Encoding.UTF8.GetByteCount(value) <= MaxValueBytes) valueId = Intern(value);
                }
                Lines.Add(new SnapLine(reference, checked((byte)depth), role, nameId, valueId));
                if (node.Password == false && node.ReadVisibleText(MaxTextBytes) is { } body) TextLines(body.Reveal(), depth, reference);
            }

            if (depth == MaxDepth || _visited >= MaxNodes) return;
            var childSiblings = new Dictionary<ulong, int>();
            foreach (var child in node.Children)
            {
                if (_visited >= MaxNodes) break;
                if (child is not null) Visit(child, depth + 1, reference, childSiblings);
            }
        }

        // Each non-empty visible line is a child line named by its text: refs follow content, so a scroll is a small diff.
        private void TextLines(string text, int depth, ulong parent)
        {
            var siblings = new Dictionary<ulong, int>();
            var bytes = 0;
            var lineDepth = checked((byte)Math.Min(depth + 1, MaxDepth));
            foreach (var raw in text.Split('\n'))
            {
                var line = Cap(raw.TrimEnd('\r', ' '), MaxNameChars)!;
                if (line.Length == 0) continue;
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
                if (bytes > MaxTextBytes || Lines.Count >= MaxNodes * 4) break;
                var identity = RefMinter.Ref(0, null, TextLineRole, line, 0);
                siblings.TryGetValue(identity, out var index);
                siblings[identity] = index + 1;
                Lines.Add(new SnapLine(RefMinter.Ref(parent, null, TextLineRole, line, index), lineDepth, TextLineRole, Intern(line), -1));
            }
        }

        private int Intern(string? value)
        {
            if (value is null) return -1;
            if (_strings.TryGetValue(value, out var existing)) return existing;
            var id = Strings.Count;
            Strings.Add(value);
            _strings.Add(value, id);
            return id;
        }
    }
}
