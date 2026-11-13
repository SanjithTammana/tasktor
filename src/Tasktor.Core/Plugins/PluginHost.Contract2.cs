using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Plugins;
using Tasktor.Core.Logging;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Plugins;

/// <summary>Carries out a plugin's request (CONTRACT S4). The host has already checked the capability; the implementation
/// routes it through the same router, guards and undo as a user action.</summary>
public interface IPluginRequests
{
    Task<PluginRequestResult> Handle(string pluginId, PluginRequest request);
}

/// <summary>One consent line: the capability's JSON name and what the user reads.</summary>
public sealed record PluginCapabilityLine(string Name, string Line);

public sealed partial class PluginEntry
{
    /// <summary>The contract-2 manifest, or null for a contract-1 plugin.</summary>
    public PluginManifest2? V2 { get; set; }

    public DateTimeOffset? WakeAt { get; set; }

    public DateTimeOffset? EventsDueAt { get; set; }

    public List<PluginEvent> PendingEvents { get; } = [];

    public int EventsDropped { get; set; }

    public List<PluginRequestResult> PendingResults { get; } = [];

    public IReadOnlyDictionary<string, JsonElement>? SavedState { get; set; }

    public PluginPage? Page { get; set; }

    public string? BrowserCard { get; set; }

    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>When the last notices were shown (rolling hour cap).</summary>
    public Queue<DateTimeOffset> NoticeTimes { get; } = new();

    /// <summary>Bumped whenever consent or the run state changes (enable, disable, pause, a changed folder). A run, or a request
    /// result, that started under an older generation is discarded when it completes.</summary>
    public long Generation { get; set; }

    /// <summary>A render is owed (just turned on or resumed), whatever its interval.</summary>
    public bool NeedsRender { get; set; }
}

public sealed partial class PluginHost
{
    public const int MaxEventsPerRun = 100;
    public const int NoticesPerHour = 12;
    public const int MaxOutstandingRequests = 10;
    public const int MaxPendingResults = 20;
    public static readonly TimeSpan EventBatch = TimeSpan.FromSeconds(2);

    /// <summary>Requests handed to the handler that have not answered, per plugin id: kept by id, not by entry, so neither a new
    /// generation nor a removed and re-added folder starts the count again.</summary>
    private readonly Dictionary<string, int> _outstanding = new(StringComparer.Ordinal);

    /// <summary>Requests of this plugin id still waiting for the handler.</summary>
    public int Outstanding(string id) => _outstanding.GetValueOrDefault(id);

    /// <summary>Capabilities this build cannot serve yet; a plugin asking for one is not loaded (it would silently get nothing).</summary>
    public IReadOnlySet<PluginCapability> Unsupported { get; set; } = new HashSet<PluginCapability>();

    /// <summary>Raised on the loop when a plugin stops being allowed to speak (turned off, paused, failed, changed): withdraw
    /// its toasts and their buttons.</summary>
    public event Action<string>? Silenced;

    /// <summary>Carries out plugin requests; null refuses them all ("Not available").</summary>
    public IPluginRequests? Requests { get; set; }

    /// <summary>The jobs as <c>read:jobs</c> shows them.</summary>
    public Func<IReadOnlyList<PluginJob>>? JobsSource { get; set; }

    /// <summary>The ports table as <c>read:listeners</c> shows it.</summary>
    public Func<IReadOnlyList<PluginListener>>? ListenersSource { get; set; }

    /// <summary>Raised on the loop for each notice that passed the caps (plugin id, notice).</summary>
    public event Action<string, PluginNotice>? Notice;

    /// <summary>What the plugin is allowed: the manifest's capabilities the user ticked. No consent, or a consent without a
    /// ticked list, grants nothing.</summary>
    public IReadOnlySet<PluginCapability> Granted(string id)
    {
        if (!_plugins.TryGetValue(id, out var e) || e.V2?.Capabilities is not { } asked
            || _consents.Current.Plugins.GetValueOrDefault(id)?.Granted is not { } ticked)
        {
            return new HashSet<PluginCapability>();
        }

        return asked.Where(c => ticked.Contains(PluginCapabilityText.Name(c), StringComparer.Ordinal)).ToHashSet();
    }

    /// <summary>Consent or run state changed: anything in flight for the old state is discarded when it lands. Outstanding
    /// requests still count against the cap until they answer, whatever the generation.</summary>
    private void Invalidate(PluginEntry e)
    {
        e.Generation++;
        Silenced?.Invoke(e.Id);
    }

    /// <summary>Drop what an earlier consent let the plugin set up: page, browser card, wake, queued events and results, warnings.</summary>
    private static void ClearEffects(PluginEntry e)
    {
        e.Page = null;
        e.BrowserCard = null;
        e.WakeAt = null;
        e.EventsDueAt = null;
        e.EventsDropped = 0;
        e.PendingEvents.Clear();
        e.PendingResults.Clear();
        e.Warnings = [];
    }

    /// <summary>The plugin's current page (<c>page</c> capability), for the Tasktor window.</summary>
    public PluginPage? Page(string id) => _plugins.GetValueOrDefault(id)?.Page;

    /// <summary>An event for contract-2 plugins: each one On with the matching capability (and watch list) gets it in a batch.</summary>
    public void Deliver(PluginEvent ev, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var changed = false;
        foreach (var e in _plugins.Values)
        {
            if (e.State != PluginLifecycle.On || e.V2 is not { } m || !Wants(m, Granted(e.Id), ev))
            {
                continue;
            }

            if (e.PendingEvents.Count >= MaxEventsPerRun)
            {
                e.EventsDropped++;
            }
            else
            {
                e.PendingEvents.Add(ev);
            }

            e.EventsDueAt ??= now + EventBatch;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>A form on the plugin's page was sent: run <c>submit</c> with its values.</summary>
    public Task<PluginRunResult> Submit(string id, string form, IReadOnlyDictionary<string, string> values, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!_plugins.TryGetValue(id, out var e) || e.V2 is null || e.State != PluginLifecycle.On)
        {
            return Task.FromResult(new PluginRunResult(false, "Plugin is off"));
        }

        if (!Granted(id).Contains(PluginCapability.Page))
        {
            return Task.FromResult(new PluginRunResult(false, "The plugin may not show a page"));
        }

        var f = (e.Page?.Sections ?? []).Select(s => s.Form).FirstOrDefault(x => x?.Id == form);
        if (f is null)
        {
            return Task.FromResult(new PluginRunResult(false, "That form is not on the plugin's page"));
        }

        if (PluginValidation.CheckSubmit(f, values) is { } problem)
        {
            return Task.FromResult(new PluginRunResult(false, problem));
        }

        if (e.Running)
        {
            return Task.FromResult(new PluginRunResult(false, "Plugin is busy; try again"));
        }

        return StartRun(e, PluginCall2.Submit, form, null, values, now);
    }

    private static bool Wants(PluginManifest2 m, IReadOnlySet<PluginCapability> granted, PluginEvent ev)
    {
        bool Watched(string exe) => m.WatchExes is not { Count: > 0 } w || w.Contains(exe, StringComparer.OrdinalIgnoreCase);
        return ev switch
        {
            ProcessStartedEvent p => granted.Contains(PluginCapability.EventsProcess) && Watched(p.Exe),
            ProcessExitedEvent p => granted.Contains(PluginCapability.EventsProcess) && Watched(p.Exe),
            WindowEvent2 w => granted.Contains(PluginCapability.EventsWindows) && Watched(w.Exe),
            ForegroundEvent => granted.Contains(PluginCapability.EventsForeground),
            NetworkEvent => granted.Contains(PluginCapability.EventsNetwork),
            BrowserTabEvent => granted.Contains(PluginCapability.BrowserTabs),
            BrowserTextEvent => granted.Contains(PluginCapability.CaptureBrowser),
            _ => false,
        };
    }

    /// <summary>The call a due contract-2 plugin should get now, or null: the one that has waited longest (a plugin whose wake
    /// keeps firing cannot starve its events, nor the reverse); a tie goes to render, then timer.</summary>
    private static PluginCall2? DueCall(PluginEntry e, DateTimeOffset now)
    {
        var due = Candidates(e).Where(c => c.At <= now).ToList();
        return due.Count == 0 ? null : due.MinBy(c => c.At).Call;
    }

    private static DateTimeOffset? DueAt2(PluginEntry e) => Candidates(e).Select(c => (DateTimeOffset?)c.At).Min();

    private static IEnumerable<(DateTimeOffset At, PluginCall2 Call)> Candidates(PluginEntry e)
    {
        if (e.Runs == 0 || e.NeedsRender || e.Manifest!.Interval != "manual")
        {
            yield return (e.NextRun, PluginCall2.Render);
        }

        if (e.WakeAt is { } w)
        {
            yield return (w, PluginCall2.Timer);
        }

        if (e.EventsDueAt is { } d && (e.PendingEvents.Count > 0 || e.EventsDropped > 0))
        {
            yield return (d, PluginCall2.Event);
        }
    }

    private string BuildInput2(PluginEntry e, PluginCall2 call, string? action, IReadOnlyDictionary<string, string>? args, IReadOnlyDictionary<string, string>? form, DateTimeOffset now, string dataDir)
    {
        var granted = Granted(e.Id);
        List<PluginEvent>? events = null;
        var dropped = 0;
        if (call == PluginCall2.Event)
        {
            events = [.. e.PendingEvents];
            dropped = e.EventsDropped;
            e.PendingEvents.Clear();
            e.EventsDropped = 0;
            e.EventsDueAt = null;
        }

        if (call == PluginCall2.Timer)
        {
            e.WakeAt = null;
        }

        if (call == PluginCall2.Render)
        {
            e.NeedsRender = false;
        }

        List<PluginRequestResult>? results = e.PendingResults.Count > 0 ? [.. e.PendingResults] : null;
        e.PendingResults.Clear();
        var input = new PluginInput2
        {
            Contract = 2,
            Call = call,
            Now = now,
            Granted = e.V2!.Capabilities?.Where(granted.Contains).ToList() ?? [],
            Action = action,
            Args = args,
            Form = form,
            Events = events,
            EventsDropped = dropped,
            Results = results,
            State = granted.Contains(PluginCapability.State) ? e.SavedState : null,
            Jobs = granted.Contains(PluginCapability.ReadJobs) ? JobsSource?.Invoke() ?? [] : null,
            Listeners = granted.Contains(PluginCapability.ReadListeners) ? ListenersSource?.Invoke() ?? [] : null,
            Settings = Settings(e.Id),
            SnapshotPath = _layout.Snapshot,
            DataDir = dataDir,
        };
        return JsonSerializer.Serialize(input, ContractsJsonContext.Default.PluginInput2);
    }

    /// <summary>Apply a successful contract-2 output; returns the run's error, if the output was refused.</summary>
    private string? Apply2(PluginEntry e, string stdout, DateTimeOffset startedAt)
    {
        var granted = Granted(e.Id);
        var (o, error) = PluginValidation.CheckOutput2(e.Manifest!, granted, stdout, startedAt);
        if (o is null)
        {
            return error;
        }

        var warnings = new List<string>(o.Warnings);
        if (o.Cards is not null)
        {
            e.Cards = o.Cards;
            e.BrowserCard = o.BrowserCard;
        }

        if (o.Page is not null)
        {
            e.Page = o.Page;
        }

        if (o.ClearWake)
        {
            e.WakeAt = null;
        }

        if (o.WakeAt is { } w)
        {
            e.WakeAt = w;
        }

        if (o.State is not null)
        {
            e.SavedState = o.State;
            AtomicFile.Write(StatePath(e.Id), o.StateBytes!);
        }

        while (e.NoticeTimes.TryPeek(out var t) && t <= startedAt - TimeSpan.FromHours(1))
        {
            e.NoticeTimes.Dequeue();
        }

        foreach (var n in o.Notices)
        {
            if (e.NoticeTimes.Count >= NoticesPerHour)
            {
                warnings.Add("Notifications limited to 12 an hour");
                break;
            }

            e.NoticeTimes.Enqueue(startedAt);
            Notice?.Invoke(e.Id, n);
        }

        foreach (var r in o.Requests)
        {
            var need = PluginValidation.Needs(r);
            if (!granted.Contains(need))
            {
                e.PendingResults.Add(new PluginRequestResult { Id = r.Id, Status = PluginRequestStatus.Refused, Message = PluginCapabilityText.Name(need) + " was not granted" });
                continue;
            }

            if (Requests is not { } handler)
            {
                AddResult(e, new PluginRequestResult { Id = r.Id, Status = PluginRequestStatus.Refused, Message = "Not available" });
                continue;
            }

            if (Outstanding(e.Id) >= MaxOutstandingRequests)
            {
                AddResult(e, new PluginRequestResult { Id = r.Id, Status = PluginRequestStatus.Refused, Message = "Too many requests are still waiting" });
                continue;
            }

            var id = e.Id;
            var generation = e.Generation;
            Task<PluginRequestResult> task;
            try
            {
                task = handler.Handle(id, r);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                AddResult(e, new PluginRequestResult { Id = r.Id, Status = PluginRequestStatus.Failed, Message = ex.Message });
                continue;
            }

            _outstanding[id] = Outstanding(id) + 1;
            _ = task.ContinueWith(
                t => _post(() =>
                {
                    if (Outstanding(id) <= 1)
                    {
                        _outstanding.Remove(id);
                    }
                    else
                    {
                        _outstanding[id]--;
                    }

                    // A result for a plugin since turned off, paused or changed is dropped: its next run belongs to a new consent.
                    if (_plugins.TryGetValue(id, out var still) && ReferenceEquals(still, e) && still.Generation == generation)
                    {
                        AddResult(still, t.IsCompletedSuccessfully && t.Result is { } res
                            ? res with { Id = r.Id }
                            : new PluginRequestResult { Id = r.Id, Status = PluginRequestStatus.Failed, Message = "The request failed" });
                        Changed?.Invoke();
                    }
                }),
                TaskScheduler.Default);
        }

        e.Warnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        if (warnings.Count > 0)
        {
            _log?.Warn("plugin.warning", LogField.Of("plugin", e.Id), LogField.Of("warning", e.Warnings[0]));
        }

        return null;
    }

    /// <summary>Queue a request result for the next run; past the cap the oldest is dropped.</summary>
    private static void AddResult(PluginEntry e, PluginRequestResult result)
    {
        if (e.PendingResults.Count >= MaxPendingResults)
        {
            e.PendingResults.RemoveAt(0);
        }

        e.PendingResults.Add(result);
    }

    private string StatePath(string id) => Path.Combine(_layout.PluginData(id), "state.json");

    private void LoadState(PluginEntry e)
    {
        if (e.SavedState is not null || !File.Exists(StatePath(e.Id)))
        {
            return;
        }

        try
        {
            e.SavedState = JsonSerializer.Deserialize(File.ReadAllBytes(StatePath(e.Id)), PluginValidation.StateInfo);
        }
        catch (JsonException)
        {
            _log?.Warn("plugin.state_unreadable", LogField.Of("plugin", e.Id));
        }
    }

    private static Card WarningCard(PluginEntry e) => new()
    {
        Id = "plugin." + e.Id + ".warnings",
        Source = new CardSource { Kind = CardSourceKind.Plugin, Plugin = e.Id, Name = e.Manifest?.Name ?? e.Id },
        Title = e.Manifest?.Name ?? e.Id,
        Icon = IconKey.Plugin,
        Severity = Value.Literal("warning"),
        Blocks = e.Warnings.Select((w, i) => (Block)new BadgeBlock { Text = Value.Literal(w), Severity = Value.Literal("warning"), Priority = i + 1 }).ToList(),
    };
}
