using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Actions;

/// <summary>Action classes (U1 D-H4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionClass>))]
public enum ActionClass
{
    [JsonStringEnumMemberName("read")] Read,
    [JsonStringEnumMemberName("reversible")] Reversible,
    [JsonStringEnumMemberName("destructive")] Destructive,
}

/// <summary>Who sent an action (U1 D-H4, U4 H-G3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Origin>))]
public enum Origin
{
    [JsonStringEnumMemberName("widget")] Widget,
    [JsonStringEnumMemberName("flyout")] Flyout,
    [JsonStringEnumMemberName("tray")] Tray,
    [JsonStringEnumMemberName("toast")] Toast,
    [JsonStringEnumMemberName("cli")] Cli,
    [JsonStringEnumMemberName("rule")] Rule,
    [JsonStringEnumMemberName("shell")] Shell,
}

/// <summary><c>tasktor.action/1</c> request: one verb for every action (U1 D-H4).</summary>
public sealed record ActionRequest
{
    public string Schema { get; init; } = ContractIds.Action;

    public required string Action { get; init; }

    public IReadOnlyDictionary<string, string>? Target { get; init; }

    public IReadOnlyDictionary<string, string>? Args { get; init; }

    public required Origin Origin { get; init; }

    /// <summary>The confirm token from a <c>needs_confirm</c> result, for the second step.</summary>
    public string? Confirm { get; init; }

    /// <summary>For toast-origin requests: the toast that offered this button (only offered actions may run).</summary>
    public string? Toast { get; init; }

    /// <summary>For rule-origin requests: the rule id.</summary>
    public string? Rule { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionStatus>))]
public enum ActionStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("partial")] Partial,
    [JsonStringEnumMemberName("refused")] Refused,
    [JsonStringEnumMemberName("needs_confirm")] NeedsConfirm,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary><c>tasktor.action/1</c> result.</summary>
public sealed record ActionResult
{
    public string Schema { get; init; } = ContractIds.Action;

    public required string Action { get; init; }

    public required ActionStatus Status { get; init; }

    /// <summary>One line for the user ("Ended Chrome: 29 processes", "Access denied: runs as admin").</summary>
    public string? Message { get; init; }

    /// <summary>For <c>needs_confirm</c>: the token, valid 30 s for this request only.</summary>
    public string? Token { get; init; }

    /// <summary>For <c>needs_confirm</c>: what the second step will do ("End Chrome (29)?").</summary>
    public string? Summary { get; init; }

    /// <summary>Machine-readable reason for refusals (e.g. <c>origin_policy</c>, <c>guard:own_user</c>).</summary>
    public string? Reason { get; init; }

    /// <summary>Per-item outcomes (per process, per slot).</summary>
    public IReadOnlyList<ActionItemResult>? Items { get; init; }

    /// <summary>For reversible actions that can be undone: the undo handle.</summary>
    public string? Undo { get; init; }
}

public sealed record ActionItemResult
{
    public required string Item { get; init; }

    public required ActionStatus Status { get; init; }

    public string? Reason { get; init; }
}
