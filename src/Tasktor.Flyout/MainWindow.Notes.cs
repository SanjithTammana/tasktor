using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tasktor.Contracts.Cards;
using Tasktor.Core.Features.Focus;
using Tasktor.Core.Storage;

namespace Tasktor.Flyout;

/// <summary>
/// E11.3 notes editor on the Focus page. The viewer edits the user's own files in <c>notes\</c> through
/// <see cref="NotesEditor"/>, so a note's text never crosses the pipe; pin, unpin and delete are agent actions (names
/// only; delete asks first), and saving a pinned note re-pins it so the focus card's lines refresh.
/// </summary>
public sealed partial class MainWindow
{
    private NotesEditor? _notesEditor;
    private NoteDraft? _draft;
    private string? _noteError;

    // The open editor survives the page's redraws (it redraws on every snapshot change): same text boxes, same caret.
    private StackPanel? _editorPanel;

    private NotesEditor Notes => _notesEditor ??= new NotesEditor(new NotesStore(StoreLayout.Resolve(null, AppContext.BaseDirectory).NotesDir));

    private void DrawNotes(StackPanel target)
    {
        var section = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        section.Children.Add(new TextBlock { Text = "Notes", Style = StyleOf("TasktorSubtitleTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
        if (_draft is { } draft)
        {
            _editorPanel ??= Editor(draft);
            if (_editorPanel.Parent is Panel old)
            {
                old.Children.Remove(_editorPanel); // still inside the previous draw's section
            }

            section.Children.Add(_editorPanel);
            target.Children.Add(section);
            return;
        }

        IReadOnlyList<NoteItem> items;
        try
        {
            items = Notes.List();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            items = [];
            section.Children.Add(Text("Notes could not be read.", "TasktorInk2Brush"));
        }

        if (items.Count == 0)
        {
            section.Children.Add(Text("No notes yet. Pinned notes show their first lines on the focus card.", "TasktorInk2Brush"));
        }

        foreach (var item in items)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = item.Name + (item.Pinned ? " (pinned)" : string.Empty), Style = StyleOf("TasktorBodyTextStyle"), Foreground = ThemeBrush("TasktorInkBrush") });
            if (item.FirstLine.Length > 0)
            {
                label.Children.Add(Caption(item.FirstLine, "TasktorInk3Brush"));
            }

            row.Children.Add(label);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) => OpenNote(item.Name);
            buttons.Children.Add(edit);
            var pin = new Button { Content = item.Pinned ? "Unpin" : "Pin" };
            pin.Click += async (_, _) => await NoteActionAsync(item.Pinned ? "note.unpin" : "note.pin", item.Name);
            buttons.Children.Add(pin);
            var delete = new Button { Content = "Delete…" };
            delete.Click += async (_, _) => await NoteActionAsync("note.delete", item.Name);
            buttons.Children.Add(delete);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            section.Children.Add(row);
        }

        var add = new Button { Content = "New note", Margin = new Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            _draft = Notes.New();
            _noteError = null;
            _editorPanel = null;
            Draw(_snapshot.Mirror.Cards);
        };
        section.Children.Add(add);
        target.Children.Add(section);
    }

    private StackPanel Editor(NoteDraft draft)
    {
        var editor = new StackPanel { Spacing = 8 };
        var name = new TextBox { Header = "Name", Text = draft.Name, MaxLength = 80 };
        var text = new TextBox { Header = "Note", Text = draft.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200, MaxHeight = 480 };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        var size = Caption(NotesEditor.Limit(draft), "TasktorInk3Brush");
        name.TextChanged += (_, _) => _draft = _draft! with { Name = name.Text };
        text.TextChanged += (_, _) =>
        {
            _draft = _draft! with { Text = text.Text };
            size.Text = NotesEditor.Limit(_draft);
        };
        editor.Children.Add(name);
        editor.Children.Add(text);
        editor.Children.Add(size);
        if (_noteError is { } error)
        {
            editor.Children.Add(Text(error, "TasktorInkBrush"));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "Save", Style = StyleOf("AccentButtonStyle") };
        save.Click += async (_, _) => await SaveNoteAsync();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) =>
        {
            _draft = null; // unsaved changes are dropped on purpose: Close, not Save
            _noteError = null;
            _editorPanel = null;
            Draw(_snapshot.Mirror.Cards);
        };
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        editor.Children.Add(buttons);
        return editor;
    }

    private void OpenNote(string name)
    {
        try
        {
            _draft = Notes.Open(name);
            _noteError = _draft is null ? "That note is gone." : null;
            _editorPanel = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _noteError = "That note could not be read.";
        }

        Draw(_snapshot.Mirror.Cards);
    }

    private async Task SaveNoteAsync()
    {
        if (_draft is not { } draft)
        {
            return;
        }

        string? error;
        NoteDraft saved;
        try
        {
            saved = Notes.Save(draft, out error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (saved, error) = (draft, "The note could not be saved.");
        }

        _noteError = error;
        _editorPanel = null; // rebuilt: shows the error, or closes
        if (error is null)
        {
            _draft = null;
            if (Notes.List().FirstOrDefault(i => i.Name == saved.SavedAs) is { Pinned: true } pinned)
            {
                await NoteActionAsync("note.pin", pinned.Name); // the focus card reads pinned lines again
            }
        }

        Draw(_snapshot.Mirror.Cards);
    }

    private async Task NoteActionAsync(string action, string name)
    {
        await RunAsync(new CardAction { Id = action, Label = action, Action = action, Target = new Dictionary<string, string> { ["note"] = name } });
        Draw(_snapshot.Mirror.Cards);
    }
}
