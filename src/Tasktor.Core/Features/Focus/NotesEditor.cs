using System.Globalization;
using System.Text;

namespace Tasktor.Core.Features.Focus;

/// <summary>One note being edited: the name it was opened under (<see cref="SavedAs"/>, null for a new one) and its current name and text.</summary>
public sealed record NoteDraft(string? SavedAs, string Name, string Text, string Original)
{
    public bool Dirty => Text != Original || Name != (SavedAs ?? string.Empty);

    public int Bytes => Encoding.UTF8.GetByteCount(Text);
}

/// <summary>A note in the list: its name, whether it is pinned to the focus card, and its first non-empty line.</summary>
public sealed record NoteItem(string Name, bool Pinned, string FirstLine);

/// <summary>
/// E11.3 quick notes, the flyout's editor. Notes are the user's own files under <c>notes\</c>; the viewer edits them
/// directly through <see cref="NotesStore"/> (same user, same folder), so a note's text never crosses the agent's pipe
/// or reaches its memory (the agent reads only pinned notes' first lines for the focus card). Pin, unpin and delete
/// stay agent actions (names only; delete keeps its two-step confirm). Nothing here logs a name or text.
/// </summary>
public sealed class NotesEditor(NotesStore store)
{
    private readonly NotesStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<NoteItem> List()
    {
        var pinned = _store.Pinned().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. _store.Names().Select(n => new NoteItem(n, pinned.Contains(n), FirstLine(_store.Read(n))))];
    }

    public NoteDraft New() => new(null, string.Empty, string.Empty, string.Empty);

    public NoteDraft? Open(string name) => _store.Read(name) is { } text ? new NoteDraft(name, name, text, text) : null;

    /// <summary>Save (and rename: a new name moves the note, never over another). Returns the saved draft, or the same draft and an error.</summary>
    public NoteDraft Save(NoteDraft draft, out string? error)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = draft.Name.Trim();
        error = name.Length == 0 ? "A note needs a name"
            : draft.Bytes > NotesStore.MaxBytes ? Limit(draft)
            : !string.Equals(name, draft.SavedAs, StringComparison.OrdinalIgnoreCase) && _store.Read(name) is not null ? "Another note already has that name"
            : null;
        if (error is not null)
        {
            return draft;
        }

        try
        {
            _store.Save(name, draft.Text);
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return draft;
        }

        if (draft.SavedAs is { } old && !string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
        {
            var wasPinned = _store.Pinned().Contains(old, StringComparer.OrdinalIgnoreCase);
            _store.Delete(old);
            if (wasPinned)
            {
                _store.Pin(name, true);
            }
        }

        return new NoteDraft(name, name, draft.Text, draft.Text);
    }

    /// <summary>The size line under the editor.</summary>
    public static string Limit(NoteDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var kb = draft.Bytes / 1024.0;
        return kb.ToString("0.#", CultureInfo.InvariantCulture) + " of 64 KB" + (draft.Bytes > NotesStore.MaxBytes ? ": too long to save" : string.Empty);
    }

    private static string FirstLine(string? text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } line ? (line.Length > 80 ? line[..80] + "…" : line) : string.Empty;
}
