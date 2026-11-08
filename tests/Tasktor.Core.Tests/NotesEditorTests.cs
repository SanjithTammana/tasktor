using Tasktor.Core.Features.Focus;

namespace Tasktor.Core.Tests;

/// <summary>E11.3: the flyout's notes editor works on the user's own files; text never goes through the agent.</summary>
public sealed class NotesEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-notes-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NotesEditor _editor;

    public NotesEditorTests() => _editor = new NotesEditor(new NotesStore(_dir));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_new_note_is_saved_listed_with_its_first_line_and_reopened()
    {
        var draft = _editor.New();
        Assert.Equal(string.Empty, draft.Name);
        draft = draft with { Name = "Groceries", Text = "\n  eggs\nmilk" };
        Assert.True(draft.Dirty);
        var saved = _editor.Save(draft, out var error);
        Assert.Null(error);
        Assert.False(saved.Dirty);
        var item = Assert.Single(_editor.List());
        Assert.Equal(("Groceries", false, "eggs"), (item.Name, item.Pinned, item.FirstLine));
        Assert.Equal("\n  eggs\nmilk", _editor.Open("Groceries")!.Text);
        Assert.Null(_editor.Open("missing"));
    }

    [Fact]
    public void Saving_checks_the_name_the_size_and_renames_by_moving()
    {
        Assert.NotNull(Error(_editor.New() with { Name = "  ", Text = "x" }));
        Assert.NotNull(Error(_editor.New() with { Name = "big", Text = new string('x', NotesStore.MaxBytes + 1) }));

        var first = _editor.Save(_editor.New() with { Name = "Draft", Text = "a" }, out _);
        var renamed = _editor.Save(first with { Name = "Final" }, out var error);
        Assert.Null(error);
        Assert.Equal(["Final"], _editor.List().Select(i => i.Name));
        Assert.Equal("Final", renamed.SavedAs);

        _editor.Save(_editor.New() with { Name = "Other", Text = "b" }, out _);
        Assert.NotNull(Error(renamed with { Name = "Other" })); // never overwrites another note
    }

    [Fact]
    public void The_dirty_count_and_byte_count_follow_the_text()
    {
        var draft = _editor.New() with { Name = "n", Text = "é" };
        Assert.Equal(2, draft.Bytes);
        Assert.Contains("64 KB", NotesEditor.Limit(draft), StringComparison.Ordinal);
    }

    private string? Error(NoteDraft d)
    {
        _editor.Save(d, out var error);
        return error;
    }
}
