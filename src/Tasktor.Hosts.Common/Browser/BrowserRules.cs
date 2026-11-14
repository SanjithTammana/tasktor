using Tasktor.Contracts.Capture;

namespace Tasktor.Hosts.Common.Browser;

/// <summary>
/// What a browser message may contain (C21 browser contract): one set of rules for every place a message enters, the
/// NMH reading the extension, the capture host reading its pipe and replaying the NMH's offline queue, so nothing that
/// skipped the NMH is trusted more than what passed it (F-PIPE2).
/// </summary>
public static class BrowserRules
{
    /// <summary>The message, if it keeps the rules; null otherwise.</summary>
    public static BrowserMessage? Check(BrowserMessage? message) =>
        message is null || message.At.Offset != TimeSpan.Zero ? null : message switch
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

    /// <summary>An absolute http / https address of at most 2048 characters whose host is the message's host.</summary>
    public static bool ValidUrl(string? url, string? host) =>
        url is null || (url.Length <= 2048 && host is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>The visible-text caps: lines, line length and total size.</summary>
    public static bool ValidText(IReadOnlyList<string>? lines) =>
        lines is not null && lines.Count <= BrowserText.MaxLines && lines.All(l => l is not null && l.Length <= BrowserText.MaxLine) &&
        lines.Sum(l => l.Length) <= BrowserText.MaxChars;

    public static bool ValidHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Length is > 0 and <= 253 && host == host.ToLowerInvariant() &&
            !host.Contains('/') && !host.Contains(':') && !host.Contains('?') && !host.Contains('#') &&
            Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }
}
