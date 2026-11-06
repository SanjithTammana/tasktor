using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;

namespace Tasktor.Nmh;

public static class BrowserValidation
{
    public static BrowserMessage? Read(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var fields = type.GetString() switch
            {
                "hello" => new[] { "type", "at", "contract", "client", "browser", "extension_version" },
                "tab" => new[] { "type", "at", "tab", "window", "host", "active", "audible", "url", "title" },
                "text" => new[] { "type", "at", "tab", "lines" },
                "media" => new[] { "type", "at", "tab", "playing", "position_s" },
                "focus" => new[] { "type", "at", "tab", "field" },
                "visibility" => new[] { "type", "at", "tab", "visible" },
                "tab_closed" => new[] { "type", "at", "tab" },
                "block_request" => new[] { "type", "at", "request_id", "op", "kind", "host", "tab", "minutes" },
                _ => Array.Empty<string>(),
            };
            if (fields.Length == 0 || root.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal)) ||
                root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            {
                return null;
            }

            var message = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.BrowserMessage);
            if (message is null || message.At.Offset != TimeSpan.Zero) return null;
            return message switch
            {
                BrowserHello h when h.Contract >= 0 && h.Client == "nmh" && h.Browser is "chrome" or "edge" && !string.IsNullOrWhiteSpace(h.ExtensionVersion) => h,
                BrowserTab t when t.Tab >= 0 && t.Window >= 0 && (t.Host is null || ValidHost(t.Host)) && ValidUrl(t.Url, t.Host) && (t.Title is null || t.Title.Length <= 300) => t,
                BrowserText x when x.Tab >= 0 && ValidText(x.Lines) => x,
                BrowserMedia m when m.Tab >= 0 && (m.PositionS is null or >= 0 and < 1e7) => m,
                BrowserFocus f when f.Tab >= 0 && f.Field is "none" or "text" or "secure" => f,
                BrowserVisibility v when v.Tab >= 0 => v,
                BrowserTabClosed c when c.Tab >= 0 => c,
                BrowserBlockRequest b when !string.IsNullOrWhiteSpace(b.RequestId) && b.Op is "add" or "remove" &&
                    ((b.Kind == "site" && b.Host is not null && ValidHost(b.Host) && b.Tab is null && b.Minutes is null) ||
                    (b.Kind == "tab" && b.Tab >= 0 && b.Host is null && (b.Minutes is null or >= 1 and <= 1440))) => b,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>An absolute http / https address of at most 2048 characters whose host is the message's host.</summary>
    public static bool ValidUrl(string? url, string? host) =>
        url is null || (url.Length <= 2048 && host is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>The visible-text caps: lines, line length and total size.</summary>
    public static bool ValidText(IReadOnlyList<string>? lines) =>
        lines is not null && lines.Count <= BrowserText.MaxLines && lines.All(l => l is not null && l.Length <= BrowserText.MaxLine) &&
        lines.Sum(l => l.Length) <= BrowserText.MaxChars;

    public static bool ValidHost(string host) => host.Length is > 0 and <= 253 && host == host.ToLowerInvariant() &&
        !host.Contains('/') && !host.Contains(':') && !host.Contains('?') && !host.Contains('#') &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
}
