using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>The end of one invocation: the agent's last result, or that the user declined the confirm step.</summary>
public sealed record ActionOutcome(ActionResult? Result, bool Declined)
{
    public bool Ok => Result?.Status is ActionStatus.Ok or ActionStatus.Partial;
}

/// <summary>
/// Sends a card action with the viewer's origin (U4) and carries the confirm token through the two-step dialog (D-H4):
/// when the agent answers <c>needs_confirm</c>, the viewer shows the summary; only a yes resends the same request with
/// the token. A row action's <c>{row}</c> target placeholder becomes the clicked row's key.
/// </summary>
public sealed class ActionInvoker
{
    private readonly Func<ActionRequest, CancellationToken, Task<ActionResult>> _act;
    private readonly Origin _origin;

    public ActionInvoker(Func<ActionRequest, CancellationToken, Task<ActionResult>> act, Origin origin)
    {
        _act = act;
        _origin = origin;
    }

    public static ActionInvoker Over(PipeClient client, Origin origin)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new ActionInvoker((req, ct) => client.ActAsync(req, ct), origin);
    }

    /// <summary>The request a card action stands for, with <c>{row}</c> filled from <paramref name="rowKey"/>.</summary>
    public ActionRequest Request(CardAction action, string? rowKey = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Action is not { Length: > 0 } id)
        {
            throw new ArgumentException("A plugin card action is sent as plugin.action by its host, not here.", nameof(action));
        }

        IReadOnlyDictionary<string, string>? target = action.Target;
        if (target is not null && target.Values.Any(v => v.Contains("{row}", StringComparison.Ordinal)))
        {
            if (rowKey is null)
            {
                throw new ArgumentException("This action needs a row.", nameof(rowKey));
            }

            target = target.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("{row}", rowKey, StringComparison.Ordinal), StringComparer.Ordinal);
        }

        return new ActionRequest { Action = id, Origin = _origin, Target = target, Args = action.Args };
    }

    /// <summary>
    /// Send <paramref name="request"/>; on <c>needs_confirm</c> ask <paramref name="confirm"/> with the agent's summary and
    /// resend with the token only on yes. A refusal or failure is returned as is, never retried.
    /// </summary>
    public async Task<ActionOutcome> InvokeAsync(ActionRequest request, Func<string, Task<bool>> confirm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirm);
        var first = await _act(request with { Origin = _origin, Confirm = null }, ct).ConfigureAwait(false);
        if (first.Status != ActionStatus.NeedsConfirm || first.Token is not { Length: > 0 } token)
        {
            return new ActionOutcome(first, false); // done, refused, failed, or the CLI's --yes path (no token)
        }

        if (!await confirm(first.Summary ?? first.Message ?? request.Action).ConfigureAwait(false))
        {
            return new ActionOutcome(first, true);
        }

        var second = await _act(request with { Origin = _origin, Confirm = token }, ct).ConfigureAwait(false);
        return new ActionOutcome(second, false);
    }

    public Task<ActionOutcome> InvokeAsync(CardAction action, string? rowKey, Func<string, Task<bool>> confirm, CancellationToken ct = default) =>
        InvokeAsync(Request(action, rowKey), confirm, ct);
}
