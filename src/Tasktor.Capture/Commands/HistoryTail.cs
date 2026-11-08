using System.Text;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Commands;

/// <summary>
/// C22.1: tails PSReadLine's history file (<c>SaveIncrementally</c>, one command per line, a multi-line command's inner
/// line ends carry a backtick). It starts at the file's end, so history from before capture is never imported, and it
/// hands back only whole commands. A file that shrank or was rewritten is taken up again from its new end, importing
/// nothing. Reads share the file with PowerShell (read, write and delete), so the shell is never blocked.
/// </summary>
public sealed class HistoryTail
{
    /// <summary>Longest command kept; longer ones are cut (a pasted script is not a command line).</summary>
    public const int MaxCommand = 4096;

    /// <summary>Most bytes read per call, so one huge append cannot stall the capture loop.</summary>
    public const int MaxRead = 256 * 1024;

    private const int Anchor = 64;

    private readonly string _path;
    private readonly StringBuilder _pending = new();
    private long _offset;
    private byte[] _anchor = [];

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
                SetEnd(fs);
                return [];
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
            return Parse(Encoding.UTF8.GetString(buffer, 0, cut + 1));
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

    private List<Sensitive<string>> Parse(string text)
    {
        var commands = new List<Sensitive<string>>();
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 && _pending.Length == 0)
            {
                continue;
            }

            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.EndsWith('`'))
            {
                Append(line[..^1]).Append('\n'); // an inner line of a multi-line command
                continue;
            }

            Append(line);
            var command = _pending.ToString();
            _pending.Clear();
            if (command.Trim().Length > 0)
            {
                commands.Add(Sensitive.Of(command));
            }
        }

        return commands;
    }

    private StringBuilder Append(string part)
    {
        var room = MaxCommand - _pending.Length;
        return room <= 0 ? _pending : _pending.Append(part.Length > room ? part[..room] : part);
    }

    private void Restart()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
