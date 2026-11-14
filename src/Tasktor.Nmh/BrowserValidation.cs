using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;
using Tasktor.Hosts.Common.Browser;

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
                "hello" => new[] { "type", "at", "contract", "client", "browser", "extension_version", "incognito" },
                "tab" => new[] { "type", "at", "tab", "window", "host", "active", "audible", "url", "title", "incognito" },
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

            return BrowserRules.Check(JsonSerializer.Deserialize(json, ContractsJsonContext.Default.BrowserMessage));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool ValidUrl(string? url, string? host) => BrowserRules.ValidUrl(url, host);

    public static bool ValidText(IReadOnlyList<string>? lines) => BrowserRules.ValidText(lines);

    public static bool ValidHost(string host) => BrowserRules.ValidHost(host);
}
