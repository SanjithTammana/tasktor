using System.Security.Cryptography;
using System.Text;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;

namespace Tasktor.Core.Actions;

/// <summary>
/// What a handler prepares on the loop: a one-line summary (for the confirm step and the log), the target's exe name
/// (logged; never a command line), the processes to guard, and the work. <see cref="Run"/> runs off the loop on the
/// processes that passed the guards; <see cref="Done"/> runs back on the loop with the result.
/// </summary>
public sealed record PreparedAction
{
    public required string Summary { get; init; }

    public string? TargetName { get; init; }

    public IReadOnlyList<ProcKey> Processes { get; init; } = [];

    public Func<IReadOnlyList<ProcKey>, ActionResult>? Run { get; init; }

    public Action<ActionResult>? Done { get; init; }

    /// <summary>Set when the handler cannot act at all (unknown target, feature off).</summary>
    public string? Refusal { get; init; }

    /// <summary>For results the loop can produce without off-loop work.</summary>
    public ActionResult? Immediate { get; init; }
}

/// <summary>Toasts register the requests their buttons carry; a toast-origin request must match one (D-H4 toast row).</summary>
public sealed class ToastOffers
{
    private readonly Dictionary<string, HashSet<string>> _byTag = new(StringComparer.Ordinal);

    public void Offer(string tag, ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_byTag.TryGetValue(tag, out var set))
        {
            _byTag[tag] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(ActionRouter.Fingerprint(request));
    }

    public void Withdraw(string tag) => _byTag.Remove(tag);

    public bool IsOffered(ActionRequest request) =>
        request.Toast is { } tag && _byTag.TryGetValue(tag, out var set) && set.Contains(ActionRouter.Fingerprint(request));
}

/// <summary>
/// The one router every action goes through (U1 D-H4): catalog → origin policy → <see cref="GuardChain"/> → confirm
/// token (30 s, this request only) → off-loop handler → log. Called on the logic loop.
/// </summary>
public sealed class ActionRouter
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(30);

    private readonly LogicLoop _loop;
    private readonly OriginPolicy _policy;
    private readonly GuardChain _guards;
    private readonly Func<ProcKey, ProcessRow?> _readProcess;
    private readonly LogSink? _log;
    private readonly Func<Func<ActionResult>, Task<ActionResult>> _offLoop;
    private readonly Dictionary<string, Func<ActionRequest, PreparedAction>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);

    public ActionRouter(
        LogicLoop loop,
        OriginPolicy policy,
        GuardChain guards,
        Func<ProcKey, ProcessRow?> readProcess,
        LogSink? log = null,
        Func<Func<ActionResult>, Task<ActionResult>>? offLoop = null)
    {
        _loop = loop;
        _policy = policy;
        _guards = guards;
        _readProcess = readProcess;
        _log = log;
        _offLoop = offLoop ?? (work => Task.Run(work));
    }

    public ToastOffers Toasts { get; } = new();

    /// <summary>Which rules are shipped built-ins (may call <c>use_fallback</c> / <c>relearn</c>).</summary>
    public Func<string?, bool> IsBuiltinRule { get; set; } = _ => false;

    /// <summary>Fires on the loop after each action completes (self-use counts, undo bookkeeping).</summary>
    public event Action<ActionRequest, ActionResult>? Completed;

    public void Register(string actionId, Func<ActionRequest, PreparedAction> handler)
    {
        if (ActionCatalog.Find(actionId) is null)
        {
            throw new ArgumentException("Not in the action catalog: " + actionId, nameof(actionId));
        }

        _handlers[actionId] = handler;
    }

    public bool HasHandler(string actionId) => _handlers.ContainsKey(actionId);

    /// <summary>Route one request. Must be called on the logic loop; the task completes on the loop.</summary>
    public Task<ActionResult> Submit(ActionRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var def = ActionCatalog.Find(req.Action);
        if (def is null)
        {
            return Finish(req, null, Refused(req, "unknown_action", "Unknown action"));
        }

        if (req.Origin == Origin.Rule && def.BuiltinRulesOnly && !IsBuiltinRule(req.Rule))
        {
            return Finish(req, null, Refused(req, "builtin_rules_only", "Only built-in rules may do this"));
        }

        var tokenValid = req.Confirm is { } t && t != OriginPolicy.CliYes && ConsumeToken(t, req);
        var (verdict, reason) = _policy.Check(def, req, Toasts.IsOffered(req), tokenValid);
        if (verdict == PolicyVerdict.Refuse)
        {
            return Finish(req, null, Refused(req, "origin_policy:" + reason, PolicyMessage(reason)));
        }

        if (!_handlers.TryGetValue(req.Action, out var handler))
        {
            return Finish(req, null, Refused(req, "no_handler", "Not available"));
        }

        PreparedAction prep;
        try
        {
            prep = handler(req);
        }
        catch (ArgumentException ex)
        {
            return Finish(req, null, Refused(req, "bad_request", ex.Message));
        }

        if (prep.Refusal is { } refusal)
        {
            return Finish(req, prep, Refused(req, "handler", refusal));
        }

        // Step 3: guards, per process.
        var allowed = new List<ProcKey>(prep.Processes.Count);
        var items = new List<ActionItemResult>();
        if (def.ProcessTargets)
        {
            foreach (var key in prep.Processes)
            {
                if (_guards.Check(req.Action, key, _readProcess(key)) is { } g)
                {
                    items.Add(new ActionItemResult { Item = key.ToString(), Status = ActionStatus.Refused, Reason = "guard:" + g.Guard + ": " + g.Reason });
                }
                else
                {
                    allowed.Add(key);
                }
            }

            if (prep.Processes.Count > 0 && allowed.Count == 0)
            {
                return Finish(req, prep, new ActionResult
                {
                    Action = req.Action,
                    Status = ActionStatus.Refused,
                    Reason = items[0].Reason,
                    Message = items[0].Reason![(items[0].Reason!.IndexOf(": ", StringComparison.Ordinal) + 2)..],
                    Items = items,
                });
            }
        }
        else
        {
            allowed.AddRange(prep.Processes);
        }

        // Step 4: two-step confirm.
        if (verdict == PolicyVerdict.NeedsConfirm)
        {
            if (req.Origin == Origin.Cli)
            {
                return Finish(req, prep, new ActionResult { Action = req.Action, Status = ActionStatus.NeedsConfirm, Summary = prep.Summary, Reason = "needs_yes", Message = "Add --yes to confirm: " + prep.Summary });
            }

            var token = IssueToken(req);
            return Finish(req, prep, new ActionResult { Action = req.Action, Status = ActionStatus.NeedsConfirm, Token = token, Summary = prep.Summary });
        }

        if (prep.Immediate is { } immediate)
        {
            return Finish(req, prep, Merge(immediate, items));
        }

        if (prep.Run is null)
        {
            return Finish(req, prep, Merge(new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = prep.Summary }, items));
        }

        // Step 5: off-loop work; result posted back to the loop.
        var tcs = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = prep.Run;
        _offLoop(() => run(allowed)).ContinueWith(
            task =>
            {
                var result = task.IsCompletedSuccessfully
                    ? task.Result
                    : new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = "Failed", Reason = task.Exception?.InnerException?.GetType().Name };
                _loop.Post(() =>
                {
                    var merged = Merge(result, items);
                    Finish(req, prep, merged).ContinueWith(t => tcs.SetResult(t.Result), TaskScheduler.Default);
                });
            },
            TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>The identity of a request for tokens and toast offers: action + sorted target + sorted args.</summary>
    public static string Fingerprint(ActionRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var sb = new StringBuilder(req.Action);
        foreach (var map in new[] { req.Target, req.Args })
        {
            sb.Append('|');
            if (map is null)
            {
                continue;
            }

            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            }
        }

        return sb.ToString();
    }

    private string IssueToken(ActionRequest req)
    {
        Prune();
        var token = "c-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        _tokens[token] = (Fingerprint(req), _loop.Clock.UtcNow + TokenLifetime);
        return token;
    }

    private bool ConsumeToken(string token, ActionRequest req)
    {
        Prune();
        if (!_tokens.TryGetValue(token, out var entry))
        {
            return false;
        }

        if (entry.Fingerprint != Fingerprint(req))
        {
            return false;
        }

        _tokens.Remove(token);
        return true;
    }

    private void Prune()
    {
        var now = _loop.Clock.UtcNow;
        foreach (var k in _tokens.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
        {
            _tokens.Remove(k);
        }
    }

    private Task<ActionResult> Finish(ActionRequest req, PreparedAction? prep, ActionResult result)
    {
        if (result.Status != ActionStatus.NeedsConfirm)
        {
            prep?.Done?.Invoke(result);
        }

        _log?.Info(
            "action",
            LogField.Of("action", req.Action),
            LogField.Of("target", prep?.TargetName),
            LogField.Of("origin", req.Origin.ToString().ToLowerInvariant()),
            LogField.Of("result", result.Status.ToString().ToLowerInvariant()),
            LogField.Of("reason", result.Reason));
        Completed?.Invoke(req, result);
        return Task.FromResult(result);
    }

    private static ActionResult Merge(ActionResult result, List<ActionItemResult> refusedItems)
    {
        if (refusedItems.Count == 0)
        {
            return result;
        }

        var items = new List<ActionItemResult>(refusedItems);
        if (result.Items is { } more)
        {
            items.AddRange(more);
        }

        var status = result.Status == ActionStatus.Ok ? ActionStatus.Partial : result.Status;
        return result with { Status = status, Items = items };
    }

    private static ActionResult Refused(ActionRequest req, string reason, string message) =>
        new() { Action = req.Action, Status = ActionStatus.Refused, Reason = reason, Message = message };

    private static string PolicyMessage(string reason) => reason switch
    {
        "rules_reversible_only" => "Rules may only call reversible actions",
        "user_only" => "Only you can start this, not a rule",
        "rules_cannot_lift_blocks" => "Only you can lift a capture block",
        "not_offered_by_toast" => "That toast did not offer this action",
        "cli_destructive_off" => "Destructive CLI actions need --yes and the setting cli.allow_destructive",
        _ => "Refused",
    };
}
