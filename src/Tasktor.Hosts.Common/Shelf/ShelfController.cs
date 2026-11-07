using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Shelf;

/// <summary>A destructive action waiting for its answer inside the sentence: the agent's summary and the action.</summary>
public sealed record ShelfConfirm(string Summary, CardAction Action);

/// <summary>
/// U4 amended D-G11: what the shelf shows and how it reacts, without any UI. The sentence is recomposed on every
/// card change; tapping a noun unfolds its actions inline (one at a time); a destructive action's two-step confirm
/// (D-H4) is asked inside the sentence, and "keep" is the default answer for every way out (Esc, focus loss, the noun
/// going away does not answer it: the agent's token expires on its own). The view calls it on its UI thread and passes
/// <c>dispatch</c> so a confirm asked from a pipe thread lands there too.
/// </summary>
public sealed class ShelfController
{
    private readonly ActionInvoker _invoker;
    private readonly Action<Action> _dispatch;
    private TaskCompletionSource<bool>? _pending;

    public ShelfController(ActionInvoker invoker, Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
        _dispatch = dispatch ?? (a => a());
        Sentence = ShelfComposer.Compose([], new BindingResolver(null));
    }

    public ShelfSentence Sentence { get; private set; }

    /// <summary>The noun whose actions are unfolded, or null.</summary>
    public ShelfNoun? Unfolded { get; private set; }

    /// <summary>The confirm being asked, or null.</summary>
    public ShelfConfirm? Confirming { get; private set; }

    /// <summary>The last action's outcome in words ("Lowered Chrome's priority."), cleared when the shelf closes.</summary>
    public string? Notice { get; private set; }

    /// <summary>An action is in flight; nouns do not unfold meanwhile.</summary>
    public bool Busy { get; private set; }

    public event Action? Changed;

    public void Update(IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        Sentence = ShelfComposer.Compose(cards, resolver);
        if (Unfolded is { } open && Confirming is null)
        {
            Unfolded = Sentence.Nouns.FirstOrDefault(n => n.Id == open.Id);
        }

        Changed?.Invoke();
    }

    /// <summary>Tap on a noun: unfold it, or fold it when it is already open. Ignored while confirming or busy.</summary>
    public void Toggle(string nounId)
    {
        if (Confirming is not null || Busy)
        {
            return;
        }

        Unfolded = Unfolded?.Id == nounId ? null : Sentence.Nouns.FirstOrDefault(n => n.Id == nounId);
        Changed?.Invoke();
    }

    /// <summary>Run one of the unfolded noun's actions through <c>act</c> (origin shelf).</summary>
    public async Task<ActionOutcome?> RunAsync(CardAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Busy || Confirming is not null)
        {
            return null;
        }

        Busy = true;
        Notice = null;
        Changed?.Invoke();
        ActionOutcome outcome;
        try
        {
            outcome = await _invoker.InvokeAsync(action, null, summary => Ask(summary, action), ct);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            outcome = new ActionOutcome(new ActionResult { Action = action.Action ?? action.Id, Status = ActionStatus.Failed, Message = "Tasktor is not running." }, false);
        }

        _dispatch(() =>
        {
            Busy = false;
            Unfolded = null;
            Notice = outcome.Declined ? null : NoticeOf(outcome.Result);
            Changed?.Invoke();
        });
        return outcome;
    }

    /// <summary>The answer to the confirm in the sentence. Only a yes resends with the token.</summary>
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

    /// <summary>
    /// Esc: back out one level (confirm → keep, unfolded → fold). Returns true when there was nothing left to back out of,
    /// so the view hides the shelf.
    /// </summary>
    public bool Escape()
    {
        if (Confirming is not null)
        {
            Answer(false);
            return false;
        }

        if (Unfolded is not null)
        {
            Unfolded = null;
            Changed?.Invoke();
            return false;
        }

        return true;
    }

    /// <summary>The shelf hid (focus loss): keep anything pending, fold, forget the notice.</summary>
    public void Closed()
    {
        Answer(false);
        Unfolded = null;
        Notice = null;
        Changed?.Invoke();
    }

    private Task<bool> Ask(string summary, CardAction action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(() =>
        {
            _pending = tcs;
            Confirming = new ShelfConfirm(summary, action);
            Changed?.Invoke();
        });
        return tcs.Task;
    }

    private static string? NoticeOf(ActionResult? r) => r switch
    {
        null => null,
        { Status: ActionStatus.Ok or ActionStatus.Partial } => r.Message,
        _ => r.Message ?? "Couldn't do that (" + (r.Reason ?? r.Status.ToString().ToLowerInvariant()) + ").",
    };
}
