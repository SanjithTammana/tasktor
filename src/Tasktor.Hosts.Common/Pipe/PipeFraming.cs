using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Pipe;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>JSON lines over a stream (internal framing, U1 D-H3): one UTF-8 message per line, capped at <see cref="MaxLineBytes"/>.</summary>
public static class PipeFraming
{
    public const int MaxLineBytes = 4 * 1024 * 1024;

    public static string Encode(PipeMessage message) => JsonSerializer.Serialize(message, ContractsJsonContext.Default.PipeMessage);

    public static PipeMessage? Decode(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, ContractsJsonContext.Default.PipeMessage);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static async Task WriteAsync(Stream stream, string line, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads lines; a line over the cap ends the connection (returns null) rather than growing without bound.</summary>
    public sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buf = new byte[64 * 1024];
        private readonly MemoryStream _line = new();
        private int _pos;
        private int _len;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_pos < _len)
                {
                    var nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
                    if (nl >= 0)
                    {
                        _line.Write(_buf, _pos, nl - _pos);
                        _pos = nl + 1;
                        return TakeLine();
                    }

                    _line.Write(_buf, _pos, _len - _pos);
                    _pos = _len;
                    if (_line.Length > MaxLineBytes)
                    {
                        return null;
                    }
                }

                _len = await stream.ReadAsync(_buf, ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0)
                {
                    return _line.Length > 0 ? TakeLine() : null;
                }
            }
        }

        private string TakeLine()
        {
            var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
            _line.SetLength(0);
            return text;
        }
    }
}
