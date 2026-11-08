using System.Text;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Commands;

/// <summary>
/// C22.1: tails PSReadLine's history file (<c>SaveIncrementally</c>, one command per line, a multi-line command's inner
/// line ends carry a backtick). It starts at the file's end, so history from before capture is never imported, and it
/// hands back only whole commands. Reads share the file with PowerShell (read, write and delete), so the shell is never blocked.
/// A file that shrank or was rewritten under the tail (PSReadLine trimming it, or the file deleted and recreated) has no
/// per-line timestamps, so what is new is decided by content: the tail remembers hashes of the commands it has seen (the
/// file's own lines at start and every line read since) and takes only the commands after the last one it knows. A trim
/// (all known) imports nothing; a recreated file (nothing known) is new from its first line.
/// </summary>
public sealed class HistoryTail
{
    /// <summary>Longest command kept; longer ones are cut (a pasted script is not a command line).</summary>
    public const int MaxCommand = 4096;

    /// <summary>Most bytes read per call, so one huge append cannot stall the capture loop.</summary>
    public const int MaxRead = 256 * 1024;

    private const int Anchor = 64;

    /// <summary>Command hashes remembered for recognising a rewritten file (PSReadLine keeps 4096 entries by default).</summary>
    public const int MaxSeen = 8192;

    private readonly string _path;
    private readonly StringBuilder _pending = new();
    private long _offset;
    private byte[] _anchor = [];
    private readonly HashSet<ulong> _seen = [];
    private readonly Queue<ulong> _seenOrder = new();

    public HistoryTail(string path)
    {
        _path = path;
        Restart();
    }

    /// <summary>The history file under the roaming profile (Windows PowerShell 5 and PowerShell 7 share it).</summary>
    public static string DefaultPath(string roamingAppData) =>
        Path.Combine(roamingAppData, "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");

    /// <summary>Commands appended since the last call, oldest first. A missing or locked file yields none.</summary>
    public IReadOnlyList<Sensitive<string>> ReadNew()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset || !AnchorHolds(fs))
            {
                return Rewritten(fs);
            }

            if (fs.Length == _offset)
            {
                return [];
            }

            fs.Position = _offset;
            var buffer = new byte[(int)Math.Min(MaxRead, fs.Length - _offset)];
            var n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var cut = Array.LastIndexOf(buffer, (byte)'\n', n - 1);
            if (cut < 0)
            {
                if (n == MaxRead)
                {
                    _offset += n; // one line longer than a read: drop it rather than stall on it
                    KeepAnchor(fs);
                }

                return [];
            }

            _offset += cut + 1;
            KeepAnchor(fs);
            var commands = Parse(Encoding.UTF8.GetString(buffer, 0, cut + 1), _pending);
            Remember(commands);
            return commands;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The file was trimmed or replaced: read its complete lines (the last <see cref="MaxRead"/> bytes), return the
    /// commands after the last one already seen, and carry on from the end of its last complete line.
    /// </summary>
    private List<Sensitive<string>> Rewritten(FileStream fs)
    {
        _pending.Clear();
        var (text, end) = CompleteLines(fs);
        var commands = Parse(text, new StringBuilder());
        var last = commands.FindLastIndex(c => _seen.Contains(Hash(c)));
        var fresh = commands.GetRange(last + 1, commands.Count - last - 1);
        Remember(commands);
        _offset = end;
        KeepAnchor(fs);
        return fresh;
    }

    /// <summary>The file's complete lines (at most the last <see cref="MaxRead"/> bytes, from a line start) and where they end.</summary>
    private static (string Text, long End) CompleteLines(FileStream fs)
    {
        var start = Math.Max(0, fs.Length - MaxRead);
        var buffer = new byte[(int)(fs.Length - start)];
        fs.Position = start;
        var n = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var cut = n == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', n - 1);
        if (cut < 0)
        {
            return (string.Empty, start);
        }

        var from = 0;
        if (start > 0 && Array.IndexOf(buffer, (byte)'\n', 0, cut + 1) is var first and >= 0)
        {
            from = first + 1; // a window that starts mid-line: begin at the next line
        }

        return (Encoding.UTF8.GetString(buffer, from, cut + 1 - from), start + cut + 1);
    }

    private void Remember(IEnumerable<Sensitive<string>> commands)
    {
        foreach (var c in commands)
        {
            var h = Hash(c);
            if (_seen.Add(h))
            {
                _seenOrder.Enqueue(h);
                if (_seenOrder.Count > MaxSeen)
                {
                    _seen.Remove(_seenOrder.Dequeue());
                }
            }
        }
    }

    private static ulong Hash(Sensitive<string> command) => Deep.XxHash64.Hash(command.Reveal());

    private List<Sensitive<string>> Parse(string text, StringBuilder pending)
    {
        var commands = new List<Sensitive<string>>();
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 && pending.Length == 0)
            {
                continue;
            }

            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.EndsWith('`'))
            {
                Append(pending, line[..^1]).Append('\n'); // an inner line of a multi-line command
                continue;
            }

            Append(pending, line);
            var command = pending.ToString();
            pending.Clear();
            if (command.Trim().Length > 0)
            {
                commands.Add(Sensitive.Of(command));
            }
        }

        return commands;
    }

    private static StringBuilder Append(StringBuilder pending, string part)
    {
        var room = MaxCommand - pending.Length;
        return room <= 0 ? pending : pending.Append(part.Length > room ? part[..room] : part);
    }

    private void Restart()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Remember(Parse(CompleteLines(fs).Text, new StringBuilder())); // known, never imported: a later trim keeps them
            SetEnd(fs);
        }
        catch (IOException)
        {
            (_offset, _anchor) = (0, []); // not there yet: everything it will hold is new
        }
        catch (UnauthorizedAccessException)
        {
            (_offset, _anchor) = (0, []);
        }
    }

    private void SetEnd(FileStream fs)
    {
        _offset = fs.Length;
        _pending.Clear();
        KeepAnchor(fs);
    }

    /// <summary>The bytes just before the offset: if they change, the file was rewritten under us.</summary>
    private void KeepAnchor(FileStream fs)
    {
        var len = (int)Math.Min(Anchor, _offset);
        var anchor = new byte[len];
        fs.Position = _offset - len;
        fs.ReadExactly(anchor);
        _anchor = anchor;
    }

    private bool AnchorHolds(FileStream fs)
    {
        if (_anchor.Length == 0)
        {
            return true;
        }

        var now = new byte[_anchor.Length];
        fs.Position = _offset - _anchor.Length;
        fs.ReadExactly(now);
        return now.AsSpan().SequenceEqual(_anchor);
    }
}
