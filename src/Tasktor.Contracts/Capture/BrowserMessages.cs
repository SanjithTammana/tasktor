using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Capture;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BrowserHello), "hello")]
[JsonDerivedType(typeof(BrowserTab), "tab")]
[JsonDerivedType(typeof(BrowserFocus), "focus")]
[JsonDerivedType(typeof(BrowserVisibility), "visibility")]
[JsonDerivedType(typeof(BrowserTabClosed), "tab_closed")]
[JsonDerivedType(typeof(BrowserText), "text")]
[JsonDerivedType(typeof(BrowserMedia), "media")]
[JsonDerivedType(typeof(BrowserBlockRequest), "block_request")]
[JsonDerivedType(typeof(BrowserWelcome), "welcome")]
[JsonDerivedType(typeof(BrowserBlocks), "blocks")]
[JsonDerivedType(typeof(BrowserAck), "ack")]
public abstract record BrowserMessage
{
    public required DateTimeOffset At { get; init; }
}

public sealed record BrowserHello : BrowserMessage
{
    public required int Contract { get; init; }
    public required string Client { get; init; }
    public required string Browser { get; init; }
    public required string ExtensionVersion { get; init; }
}

public sealed record BrowserTab : BrowserMessage
{
    public required int Tab { get; init; }
    public required int Window { get; init; }
    private string? _host;

    public string? Host
    {
        get => _host;
        init => _host = BrowserHost.Validate(value);
    }
    public required bool Active { get; init; }
    public required bool Audible { get; init; }

    /// <summary>C D-C22 T1: the full address (http / https only), unless the host is excluded. Personal data.</summary>
    public string? Url { get; init; }

    /// <summary>The tab's title. Personal data.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// C18 / §5.5: the visible text of the tab's page (readable content + viewport text), sent on load + 2 s dwell and then at
/// most every 2 s while visible and changed. Never input values, password or card fields. The capture host diffs it with
/// the same differ as UIA views. Personal data.
/// </summary>
public sealed record BrowserText : BrowserMessage
{
    public const int MaxLines = 400;
    public const int MaxLine = 500;
    public const int MaxChars = 8192;

    public required int Tab { get; init; }

    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>C20.2: a tab's video is playing or paused, and where (throttled to one message per 10 s while playing).</summary>
public sealed record BrowserMedia : BrowserMessage
{
    public required int Tab { get; init; }

    public required bool Playing { get; init; }

    public double? PositionS { get; init; }
}

public sealed record BrowserFocus : BrowserMessage
{
    public required int Tab { get; init; }
    public required string Field { get; init; }
}

public sealed record BrowserVisibility : BrowserMessage
{
    public required int Tab { get; init; }
    public required bool Visible { get; init; }
}

public sealed record BrowserTabClosed : BrowserMessage
{
    public required int Tab { get; init; }
}

public sealed record BrowserBlockRequest : BrowserMessage
{
    public required string RequestId { get; init; }
    public required string Op { get; init; }
    public required string Kind { get; init; }
    private string? _host;

    public string? Host
    {
        get => _host;
        init => _host = BrowserHost.Validate(value);
    }
    public int? Tab { get; init; }
    public int? Minutes { get; init; }
}

public sealed record BlockState
{
    public required bool Paused { get; init; }
    public DateTimeOffset? PausedUntil { get; init; }
    public required IReadOnlyList<string> ExcludedHosts { get; init; }
    public required IReadOnlyList<int> PausedTabs { get; init; }
}

public sealed record BrowserWelcome : BrowserMessage
{
    public required int Contract { get; init; }
    public required BlockState Blocks { get; init; }
}

public sealed record BrowserBlocks : BrowserMessage
{
    public required BlockState State { get; init; }
}

public sealed record BrowserAck : BrowserMessage
{
    public required string RequestId { get; init; }
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
}

internal static class BrowserHost
{
    internal static string? Validate(string? host)
    {
        if (host is not null && (host.Length == 0 || host.Length > 253 || host != host.ToLowerInvariant() ||
            host.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))))
        {
            throw new System.Text.Json.JsonException("Invalid hostname");
        }

        return host;
    }
}
