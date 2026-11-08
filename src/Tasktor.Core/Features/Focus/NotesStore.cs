using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Focus;

[JsonSerializable(typeof(List<string>))]
internal sealed partial class NotesJsonContext : JsonSerializerContext;

/// <summary>
/// E11.3 quick notes: a few plain-text files under <c>notes\</c>, edited in the flyout; cards show the first lines of
/// pinned notes. Notes are the user's own content: <c>forget all</c> keeps them, <c>settings export --include-notes</c>
/// includes them, and nothing is ever sent anywhere. Nothing here logs a note's name or text.
/// </summary>
public sealed class NotesStore
{
    public const int MaxNotes = 100;
    public const int MaxBytes = 64 * 1024;
    private readonly string _dir;

    public NotesStore(string dir) => _dir = dir;

    private string PinsPath => Path.Combine(_dir, ".pinned.json");

    public static string FileName(string name) => StoreLayout.SafeName(name.Trim()) + ".txt";

    public IReadOnlyList<string> Names() => !Directory.Exists(_dir) ? [] :
        [.. Directory.EnumerateFiles(_dir, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];

    public string? Read(string name)
    {
        var path = Path.Combine(_dir, FileName(name));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Save(string name, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            throw new ArgumentException("A note can be up to 64 KB.");
        }

        var path = Path.Combine(_dir, FileName(name));
        if (!File.Exists(path) && Names().Count >= MaxNotes)
        {
            throw new ArgumentException("Up to " + MaxNotes + " notes.");
        }

        Directory.CreateDirectory(_dir);
        AtomicFile.Write(path, Encoding.UTF8.GetBytes(text));
    }

    public void Delete(string name)
    {
        File.Delete(Path.Combine(_dir, FileName(name)));
        Pin(name, false);
    }

    public IReadOnlyList<string> Pinned()
    {
        try
        {
            var names = File.Exists(PinsPath) ? JsonSerializer.Deserialize(File.ReadAllBytes(PinsPath), NotesJsonContext.Default.ListString) ?? [] : [];
            return [.. names.Where(n => File.Exists(Path.Combine(_dir, FileName(n))))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Pin(string name, bool on)
    {
        var pins = Pinned().Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on)
        {
            pins.Add(name);
        }

        Directory.CreateDirectory(_dir);
        AtomicFile.Write(PinsPath, JsonSerializer.SerializeToUtf8Bytes(pins, NotesJsonContext.Default.ListString));
    }

    /// <summary>The first lines of each pinned note, for the focus card.</summary>
    public List<string> PinnedLines(int linesPerNote = 2) =>
        [.. Pinned().SelectMany(n => (Read(n) ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Take(linesPerNote))];
}
