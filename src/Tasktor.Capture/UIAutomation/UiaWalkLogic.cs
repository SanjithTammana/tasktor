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
            var name = node.Name?.Reveal();
            var automationId = node.AutomationId?.Reveal();
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
            }

            if (depth == MaxDepth || _visited >= MaxNodes) return;
            var childSiblings = new Dictionary<ulong, int>();
            foreach (var child in node.Children)
            {
                if (_visited >= MaxNodes) break;
                if (child is not null) Visit(child, depth + 1, reference, childSiblings);
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
