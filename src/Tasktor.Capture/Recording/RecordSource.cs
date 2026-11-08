using System.Text.Json;
using Tasktor.Core.Apps;

namespace Tasktor.Capture.Recording;

/// <summary>
/// C21.4: which apps and site a vault record came from, so <c>forget</c> by app or site can find it. The recorder puts a
/// <c>"src": { "apps": [...], "host": "..." }</c> member first in each record's JSON (inside the encryption, like the rest);
/// an activity document instead lists every app and site it includes in its own <c>apps</c> and <c>hosts</c>, and is
/// forgotten whole by any of them. Readers that do not know <c>src</c> skip it.
/// </summary>
public static class RecordSource
{
    /// <summary>The body with its source spliced in as the first member (unchanged when there is nothing to say).</summary>
    public static byte[] Tag(byte[] body, IReadOnlyList<string?> apps, string? host)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(apps);
        var named = apps.OfType<string>().Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if ((named.Count == 0 && host is not { Length: > 0 }) || body.Length < 2 || body[0] != (byte)'{')
        {
            return body;
        }

        using var ms = new MemoryStream(body.Length + 64);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("src");
            w.WriteStartArray("apps");
            foreach (var a in named)
            {
                w.WriteStringValue(a);
            }

            w.WriteEndArray();
            if (host is { Length: > 0 })
            {
                w.WriteString("host", host);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        var head = ms.ToArray(); // {"src":{...}}
        var rest = body.AsSpan(1); // the original members and its closing brace
        var empty = rest.Trim(" \t\r\n"u8).SequenceEqual("}"u8);
        var result = new byte[head.Length - 1 + (empty ? 0 : 1) + rest.Length];
        head.AsSpan(0, head.Length - 1).CopyTo(result);
        var at = head.Length - 1;
        if (!empty)
        {
            result[at++] = (byte)',';
        }

        rest.CopyTo(result.AsSpan(at));
        return result;
    }

    /// <summary>True when the record came from <paramref name="app"/> (an exe glob) or <paramref name="host"/> (or a subdomain of it).</summary>
    public static bool Matches(ReadOnlySpan<byte> body, string? app, string? host)
    {
        if (app is null && host is null)
        {
            return false;
        }

        var (apps, hosts) = Read(body);
        return (app is not null && apps.Any(a => Glob.IsMatch(app, a))) || (host is not null && hosts.Any(h => HostMatches(host, h)));
    }

    /// <summary>A site block's rule: the host itself or any subdomain of it.</summary>
    public static bool HostMatches(string forget, string seen) =>
        seen.Equals(forget, StringComparison.OrdinalIgnoreCase) || seen.EndsWith("." + forget, StringComparison.OrdinalIgnoreCase);

    /// <summary>The apps and hosts a record names: its <c>src</c> member, or a document's <c>apps</c> / <c>hosts</c>.</summary>
    public static (List<string> Apps, List<string> Hosts) Read(ReadOnlySpan<byte> body)
    {
        var apps = new List<string>();
        var hosts = new List<string>();
        try
        {
            var r = new Utf8JsonReader(body);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
            {
                return (apps, hosts);
            }

            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "src" when r.TokenType == JsonTokenType.StartObject:
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            var inner = r.GetString();
                            r.Read();
                            if (inner == "apps")
                            {
                                Strings(ref r, apps);
                            }
                            else if (inner == "host" && r.TokenType == JsonTokenType.String)
                            {
                                hosts.Add(r.GetString()!);
                            }
                            else
                            {
                                r.Skip();
                            }
                        }

                        break;
                    case "apps":
                        Strings(ref r, apps);
                        break;
                    case "hosts":
                        Strings(ref r, hosts);
                        break;
                    default:
                        r.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // a record that is not JSON names no source
        }

        return (apps, hosts);
    }

    private static void Strings(ref Utf8JsonReader r, List<string> into)
    {
        if (r.TokenType != JsonTokenType.StartArray)
        {
            r.Skip();
            return;
        }

        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.String)
            {
                into.Add(r.GetString()!);
            }
            else
            {
                r.Skip();
            }
        }
    }
}
