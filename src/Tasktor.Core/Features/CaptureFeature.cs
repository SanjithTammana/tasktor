using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Rules;
using Tasktor.Core.Time;

namespace Tasktor.Core.Features;

/// <summary>
/// T1 capture, agent side (Module <c>capture</c>, card <c>core.capture</c>, U4 D-G6): the card and the <c>capture.*</c>
/// actions. Capture itself runs in <c>tasktor-capture</c>; this feature sends it <see cref="CaptureCommand"/>s through
/// <see cref="Link"/> and shows the <see cref="CaptureStatus"/> it returns or pushes. Rules may add blocks (pause,
/// exclude) but never lift one: the router refuses rule-origin <c>capture.resume</c> / <c>capture.unexclude</c>, and
/// commands carry <c>by_user</c> so the host can tell. Forget is destructive (two-step).
/// </summary>
public sealed class CaptureFeature : Feature
{
    public static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(5);
    public const string Foreground = "{foreground}";

    public CaptureFeature(FeatureContext context, Func<CaptureCommand, CancellationToken, Task<CaptureStatus>>? link)
        : base(context) => Link = link;

    public override string Id => "capture";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The capture host link (the capture pipe); null while the host is not running.</summary>
    public Func<CaptureCommand, CancellationToken, Task<CaptureStatus>>? Link { get; set; }

    /// <summary>The foreground app's exe, for "Don't capture this app" (target <c>app = {foreground}</c>).</summary>
    public Func<string?> ForegroundApp { get; set; } = () => null;

    public CaptureStatus? Status { get; private set; }

    /// <summary>
    /// Sets up the companion for a browser (<c>chrome</c> / <c>edge</c>): registers the native host for this user, opens the
    /// extension folder and the browser's extensions page. Runs off the loop; false = it could not. Null = not on this PC.
    /// </summary>
    public Func<string, bool>? SetupExtension { get; set; }

    /// <summary>The browser offered the extension (C §5.5: in front for 60 s with no connection), until it connects.</summary>
    public string? ExtensionOffer { get; private set; }

    private static readonly TimeSpan OfferAfter = TimeSpan.FromSeconds(60);
    private (string Browser, DateTimeOffset Since)? _browserInFront;

    /// <summary>Starts and restarts the capture host while this Module is on (D-H9); null = started elsewhere (tests, dev).</summary>
    public CaptureSupervisor? Supervisor
    {
        get => _supervisor;
        init
        {
            _supervisor = value;
            if (value is not null)
            {
                value.Changed += () =>
                {
                    Refresh();
                    if (value.Running)
                    {
                        _ = Context.Loop.At(Context.Clock.UtcNow + TimeSpan.FromSeconds(2), Refresh); // the host's pipes come up
                    }
                };
            }
        }
    }

    private readonly CaptureSupervisor? _supervisor;
    private IDisposable? _refresh;

    protected override void OnEnable()
    {
        Context.Router.Register("capture.pause", Pause);
        Context.Router.Register("capture.resume", req => Command(req, "Resume capture", new CaptureCommand { Verb = CaptureVerb.Resume, ByUser = ByUser(req) }));
        Context.Router.Register("capture.exclude", Exclude);
        Context.Router.Register("capture.unexclude", Unexclude);
        Context.Router.Register("capture.tab_pause", TabPause);
        Context.Router.Register("capture.forget", Forget);
        Context.Router.Register("capture.extension_setup", Setup);
        _supervisor?.Start();
        Refresh();
        _refresh = Context.Loop.Every(TimeSpan.FromMinutes(1), Refresh); // pauses expire host-side; one cheap call a minute
    }

    protected override void OnDisable()
    {
        _supervisor?.Stop();
        _refresh?.Dispose();
        Status = null;
        Context.UpdateState(s => s with { Capture = null });
    }

    /// <summary>Ask the host for its state (off the loop); no-op while it is not running.</summary>
    public void Refresh()
    {
        if (Link is not { } link)
        {
            Apply(null);
            return;
        }

        Context.OffLoop(() =>
        {
            var status = Send(link, new CaptureCommand { Verb = CaptureVerb.Status });
            Context.Loop.Post(() => Apply(status));
        });
    }

    /// <summary>A status from the host (a reply, or pushed on change). Null = the host is not reachable.</summary>
    public void Apply(CaptureStatus? status)
    {
        Status = status;
        var state = status is null ? new CaptureState { On = false } : new CaptureState
        {
            On = status.On,
            PausedUntil = status.PausedUntil,
            PausedIndefinitely = status.PausedIndefinitely,
            Blocks = status.Blocks.Count == 0 ? null : [.. status.Blocks.Select(b => b.Text)],
            RecordsToday = status.RecordsToday,
        };
        Context.UpdateState(s => s with { Capture = state });
        Context.RulesDirty();
        WatchExtension(status);
    }

    /// <summary>
    /// C §5.5 failure table: a Chromium browser in front for 60 s while its extension is not connected → the card offers
    /// to set it up (the browser keeps T0's hostname meanwhile). Sampled on each status (the 1-min refresh; no timer of its
    /// own). The offer stays until that browser connects.
    /// </summary>
    private void WatchExtension(CaptureStatus? status)
    {
        if (status is not { On: true, Extensions: { } connected })
        {
            _browserInFront = null;
            return; // capture not running, or a host that does not report extensions
        }

        if (ExtensionOffer is { } offered && connected.Contains(offered))
        {
            ExtensionOffer = null;
        }

        var browser = BrowserOf(ForegroundApp());
        var now = Context.Clock.UtcNow;
        if (browser is null || connected.Contains(browser))
        {
            _browserInFront = null;
            return;
        }

        if (_browserInFront is not { } front || front.Browser != browser)
        {
            _browserInFront = (browser, now);
        }
        else if (now - front.Since >= OfferAfter)
        {
            ExtensionOffer = browser;
        }
    }

    private static string? BrowserOf(string? exe) => exe?.ToLowerInvariant() switch
    {
        "chrome.exe" => "chrome",
        "msedge.exe" => "edge",
        _ => null,
    };

    private static string BrowserName(string browser) => browser == "edge" ? "Edge" : "Chrome";

    private PreparedAction Setup(ActionRequest req)
    {
        var browser = req.Args?.GetValueOrDefault("browser");
        if (browser is not ("chrome" or "edge"))
        {
            return new PreparedAction { Summary = "Set up the browser extension", Refusal = "Which browser: chrome or edge?" };
        }

        if (SetupExtension is not { } setup)
        {
            return new PreparedAction { Summary = "Set up the browser extension", Refusal = "Not available on this PC" };
        }

        return new PreparedAction
        {
            Summary = "Set up the " + BrowserName(browser) + " extension",
            Run = _ => setup(browser)
                ? new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = "Turn on Developer mode, choose Load unpacked and pick the folder that opened" }
                : new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = "Could not set up the extension" },
        };
    }

    private static bool ByUser(ActionRequest req) => req.Origin != Origin.Rule;

    private static CaptureStatus? Send(Func<CaptureCommand, CancellationToken, Task<CaptureStatus>> link, CaptureCommand cmd)
    {
        using var cts = new CancellationTokenSource(LinkTimeout);
        try
        {
            return link(cmd, cts.Token).WaitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    private PreparedAction Command(ActionRequest req, string summary, CaptureCommand cmd)
    {
        if (Link is not { } link)
        {
            return new PreparedAction { Summary = summary, Refusal = "Capture is not running" };
        }

        CaptureStatus? status = null;
        return new PreparedAction
        {
            Summary = summary,
            Run = _ =>
            {
                status = Send(link, cmd);
                return status switch
                {
                    null => new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = "The capture host did not answer" },
                    { Refusal: { } why } => new ActionResult { Action = req.Action, Status = ActionStatus.Refused, Reason = "capture:" + why, Message = Words(why) },
                    _ => new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = Done(cmd, status) },
                };
            },
            Done = _ =>
            {
                if (status is not null)
                {
                    Apply(status);
                }
            },
        };
    }

    /// <summary>The error card's words for a supervisor failure (turning the Module off and on again retries).</summary>
    public static string FailureWords(string failure) => failure switch
    {
        "no_consent" => "Capture needs your consent first",
        "refused" => "Capture stopped: no consent on record",
        "crashed" => "Capture stopped after repeated crashes; turn the Module off and on to retry",
        "start_failed" => "Capture could not start; turn the Module off and on to retry",
        _ => "Capture is not running",
    };

    private static string Words(string refusal) => refusal switch
    {
        "rule_owned" => "A rule added this block; lift it from the card",
        "not_found" => "That block is gone",
        _ => "The capture host refused: " + refusal,
    };

    private static string Done(CaptureCommand cmd, CaptureStatus s) => cmd.Verb switch
    {
        CaptureVerb.Pause => cmd.Until is { } u ? "Capture paused until " + u.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "Capture paused until you resume it",
        CaptureVerb.Resume => "Capture resumed",
        CaptureVerb.Exclude => "Not capturing " + (cmd.App ?? cmd.Site),
        CaptureVerb.Unexclude => "Capturing it again",
        CaptureVerb.TabPause => "Paused for this tab",
        CaptureVerb.Forget => "Forgot " + (s.Forgotten ?? 0).ToString(CultureInfo.InvariantCulture) + " records",
        _ => "Ok",
    };

    /// <summary><c>for</c>: a duration (<c>15m</c>, <c>1h</c>), <c>today</c> (until local midnight) or <c>indef</c> / none (until resumed).</summary>
    private PreparedAction Pause(ActionRequest req)
    {
        var span = req.Args?.GetValueOrDefault("for");
        DateTimeOffset? until;
        var now = Context.Clock.UtcNow;
        if (span is null or "indef" or "")
        {
            until = null;
        }
        else if (span == "today")
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
            var midnight = local.Date.AddDays(1);
            until = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        }
        else if (DurationText.TryParse(span, out var d) && d > TimeSpan.Zero && d <= TimeSpan.FromDays(7))
        {
            until = now + d;
        }
        else
        {
            return new PreparedAction { Summary = "Pause capture", Refusal = "Pause for 15m, 1h, today or until resumed" };
        }

        return Command(req, "Pause capture", new CaptureCommand { Verb = CaptureVerb.Pause, ByUser = ByUser(req), Until = until });
    }

    private PreparedAction Exclude(ActionRequest req)
    {
        var app = req.Args?.GetValueOrDefault("app") ?? req.Target?.GetValueOrDefault("app");
        var site = req.Args?.GetValueOrDefault("site") ?? req.Target?.GetValueOrDefault("site");
        if (app == Foreground)
        {
            app = ForegroundApp();
        }

        if (app is null && site is null)
        {
            return new PreparedAction { Summary = "Don't capture", Refusal = "Which app or site?" };
        }

        if ((app is { } a && !System.Text.RegularExpressions.Regex.IsMatch(a, @"^[\w.\-*? ]{1,128}$")) || (site is { } h && !Workflow.PrivacyGate.ValidHost(h)))
        {
            return new PreparedAction { Summary = "Don't capture", Refusal = "Not an app name or a hostname" };
        }

        return Command(req, "Don't capture " + (app ?? site), new CaptureCommand { Verb = CaptureVerb.Exclude, ByUser = ByUser(req), App = app?.ToLowerInvariant(), Site = site?.ToLowerInvariant() });
    }

    private PreparedAction Unexclude(ActionRequest req) =>
        req.Target?.GetValueOrDefault("block") is { Length: > 0 and <= 64 } id
            ? Command(req, "Capture again", new CaptureCommand { Verb = CaptureVerb.Unexclude, ByUser = ByUser(req), BlockId = id })
            : new PreparedAction { Summary = "Capture again", Refusal = "Which block?" };

    private PreparedAction TabPause(ActionRequest req) =>
        int.TryParse(req.Target?.GetValueOrDefault("tab"), NumberStyles.None, CultureInfo.InvariantCulture, out var tab)
            ? Command(req, "Pause for this tab", new CaptureCommand { Verb = CaptureVerb.TabPause, ByUser = ByUser(req), Tab = tab })
            : new PreparedAction { Summary = "Pause for this tab", Refusal = "Which tab?" };

    /// <summary><c>since</c>: <c>15m</c>, <c>1h</c> or <c>today</c> (U4 D-G6 "forget the last …").</summary>
    private PreparedAction Forget(ActionRequest req)
    {
        var since = req.Args?.GetValueOrDefault("since") ?? "1h";
        var now = Context.Clock.UtcNow;
        DateTimeOffset from;
        if (since == "today")
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
            from = new DateTimeOffset(local.Date, TimeZoneInfo.Local.GetUtcOffset(local.Date));
        }
        else if (DurationText.TryParse(since, out var d) && d > TimeSpan.Zero && d <= TimeSpan.FromDays(7))
        {
            from = now - d;
        }
        else
        {
            return new PreparedAction { Summary = "Forget", Refusal = "Forget the last 15m, 1h or today" };
        }

        return Command(req, "Forget captured records since " + from.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), new CaptureCommand { Verb = CaptureVerb.Forget, ByUser = ByUser(req), Since = from });
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("capture_on", Status is { On: true, PausedUntil: null, PausedIndefinitely: false });
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var s = Status;
        var failure = s is null ? _supervisor?.Failure : null;
        var badge = failure is not null ? FailureWords(failure)
            : s is null ? "Capture is not running"
            : !s.On ? "Capture off"
            : s.PausedIndefinitely ? "Paused until you resume"
            : s.PausedUntil is { } u ? "Paused until " + u.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : "Recording";
        var blocks = new List<Block>
        {
            new BadgeBlock { Text = Value.Literal(badge), Severity = Value.Literal(failure is "crashed" or "start_failed" ? "critical" : s is { On: true } && s.PausedUntil is null && !s.PausedIndefinitely ? "warn" : "normal"), Priority = 1 },
            new MetricBlock { Label = "Records today", Value = Value.Binding("/state/capture/records_today"), Priority = 3 },
        };
        if (s is { Blocks.Count: > 0 })
        {
            blocks.Insert(1, new ListBlock { Label = "Active blocks", Items = Value.Binding("/state/capture/blocks"), Max = 4, Priority = 2 });
        }

        if (ExtensionOffer is { } offer)
        {
            blocks.Insert(1, new AlertBlock { Text = Value.Literal("Page text in " + BrowserName(offer) + " needs the Tasktor companion extension"), Severity = Value.Literal("normal"), Priority = 2 });
        }

        var paused = s is { PausedIndefinitely: true } or { PausedUntil: not null };
        return
        [
            new Card
            {
                Id = "core.capture",
                Source = CardKit.Core,
                Title = "Capture",
                Icon = IconKey.Capture,
                Blocks = blocks,
                Actions =
                [
                    paused
                        ? new CardAction { Id = "resume", Label = "Resume", Action = "capture.resume", Priority = 1 }
                        : new CardAction { Id = "pause", Label = "Pause 1 h", Action = "capture.pause", Args = CardKit.Target("for", "1h"), Priority = 1 },
                    new CardAction { Id = "exclude", Label = "Don't capture this app", Action = "capture.exclude", Target = CardKit.Target("app", Foreground), Priority = 2 },
                    new CardAction { Id = "forget", Label = "Forget last hour", Action = "capture.forget", Args = CardKit.Target("since", "1h"), Priority = 3, Destructive = true },
                    .. ExtensionOffer is { } b
                        ? new[] { new CardAction { Id = "extension", Label = "Set up the extension", Action = "capture.extension_setup", Args = CardKit.Target("browser", b), Priority = 2 } }
                        : [],
                ],
            },
        ];
    }
}
