using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Command;

/// <summary>
/// U4 amended D-G12: the command line without UI. Typing re-queries <see cref="CommandIndex"/>; Up / Down move the
/// highlight across sections; Enter runs it through <c>act</c> with origin <c>command</c> (a destructive action asks its
/// two-step confirm in place, "keep" being the answer for Esc) or asks the view to open a page. Like
/// <see cref="Shelf.ShelfController"/>, the view calls it on its UI thread and passes <c>dispatch</c>.
/// </summary>
public sealed class CommandController
{
    private readonly ActionInvoker _invoker;
    private readonly Action<Action> _dispatch;
    private IReadOnlyList<Card> _cards = [];
    private BindingResolver _resolver = new(null);
    private TaskCompletionSource<bool>? _pending;

    public CommandController(ActionInvoker invoker, Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
        _dispatch = dispatch ?? (a => a());
        View = CommandIndex.Query(null, [], _resolver);
    }

    public string Text { get; private set; } = string.Empty;

    public CommandView View { get; private set; }

    /// <summary>Index into the flattened results; -1 when there are none.</summary>
    public int Selected { get; private set; } = -1;

    public CommandItem? Highlighted => Selected >= 0 ? Items.ElementAtOrDefault(Selected) : null;

    public IReadOnlyList<CommandItem> Items => [.. View.Sections.SelectMany(s => s.Items)];

    /// <summary>The agent's summary while a destructive action waits for its answer.</summary>
    public string? Confirming { get; private set; }

    public string? Notice { get; private set; }

    public bool Busy { get; private set; }

    public event Action? Changed;

    /// <summary>The view should open this deep-window page and close the command line.</summary>
    public event Action<string>? OpenPage;

    /// <summary>The command line is done (an action ran); the view closes it.</summary>
    public event Action? Done;

    public void Update(IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        _cards = cards ?? [];
        _resolver = resolver ?? new BindingResolver(null);
        Requery(keepSelection: true);
    }

    public void Type(string text)
    {
        Text = text ?? string.Empty;
        Notice = null;
        Requery(keepSelection: false);
    }

    public void Move(int delta)
    {
        var n = Items.Count;
        if (n == 0 || Confirming is not null)
        {
            return;
        }

        Selected = ((Selected + delta) % n + n) % n;
        Changed?.Invoke();
    }

    /// <summary>Enter: run the highlighted item, or answer yes to a pending confirm.</summary>
    public async Task<ActionOutcome?> EnterAsync(CancellationToken ct = default)
    {
        if (Confirming is not null)
        {
            Answer(true);
            return null;
        }

        if (Busy || Highlighted is not { } item)
        {
            return null;
        }

        if (item.Page is { } page)
        {
            OpenPage?.Invoke(page);
            return null;
        }

        if (item.Action is not { } action)
        {
            return null;
        }

        Busy = true;
        Changed?.Invoke();
        ActionOutcome outcome;
        try
        {
            outcome = await _invoker.InvokeAsync(action, null, Ask, ct);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            outcome = new ActionOutcome(new ActionResult { Action = action.Action ?? action.Id, Status = ActionStatus.Failed, Message = "Tasktor is not running." }, false);
        }

        _dispatch(() =>
        {
            Busy = false;
            if (outcome.Ok)
            {
                Notice = null;
                Done?.Invoke();
            }
            else
            {
                Notice = outcome.Declined ? null : outcome.Result?.Message ?? "Couldn't do that (" + (outcome.Result?.Reason ?? "failed") + ").";
            }

            Changed?.Invoke();
        });
        return outcome;
    }

    public void Answer(bool yes)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        Confirming = null;
        Changed?.Invoke();
        pending.TrySetResult(yes);
    }

    /// <summary>Esc: a pending confirm is kept (answered no); otherwise true, so the view closes.</summary>
    public bool Escape()
    {
        if (Confirming is not null)
        {
            Answer(false);
            return false;
        }

        return true;
    }

    /// <summary>The command line closed (Esc or focus loss): keep anything pending and start empty next time.</summary>
    public void Closed()
    {
        Answer(false);
        Notice = null;
        Type(string.Empty);
    }

    private Task<bool> Ask(string summary)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(() =>
        {
            _pending = tcs;
            Confirming = summary;
            Changed?.Invoke();
        });
        return tcs.Task;
    }

    private void Requery(bool keepSelection)
    {
        var before = Highlighted;
        View = CommandIndex.Query(Text, _cards, _resolver);
        var items = Items;
        var again = keepSelection && before is not null ? items.ToList().FindIndex(i => i.Label == before.Label) : -1;
        Selected = again >= 0 ? again : items.Count > 0 ? 0 : -1;
        Changed?.Invoke();
    }
}
