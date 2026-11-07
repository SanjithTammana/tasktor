using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.Guards;

/// <summary>
/// I3 Meeting guard (Module <c>meeting_guard</c>): state <c>in_call</c> = a microphone is in use by an app in the user's
/// <c>call_apps</c> group, or by a browser while the calendar says a meeting is on (<c>meeting_now</c>). If the microphone
/// route is unavailable, <c>in_call</c> = <c>meeting_now</c> and the guards card says so. The default rules pause refits
/// (on) and lower opted-in groups (off), with undo; nothing is paused in a call unless the user turned it on (OQ-B17).
/// </summary>
public sealed class MeetingGuard : Feature
{
    public static readonly string[] Browsers = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe"];

    private readonly IEventSource<CallState>? _source;
    private readonly Func<bool> _meetingNow;
    private bool _lastInCall;

    public MeetingGuard(FeatureContext context, IEventSource<CallState>? source, Func<bool> meetingNow)
        : base(context)
    {
        _source = source;
        _meetingNow = meetingNow;
    }

    public override string Id => "meeting_guard";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The call apps group from the rules file (default Teams, Zoom, Webex, Discord, Slack).</summary>
    public Func<IReadOnlyList<string>> CallApps { get; set; } = () => [];

    public CallState? Last { get; private set; }

    public bool CalendarOnly => _source is null || Last is { Available: false };

    protected override void OnEnable() => _source?.Start(s => Context.Loop.Post(() => Apply(s)));

    protected override void OnDisable()
    {
        _source?.Stop();
        Last = null;
    }

    public void Apply(CallState state)
    {
        Last = state;
        if (InCall() != _lastInCall)
        {
            _lastInCall = !_lastInCall;
            Context.Log.Info("meeting_guard.in_call", Logging.LogField.Of("on", _lastInCall)); // never which app
            Context.RulesDirty();
        }
    }

    public bool InCall()
    {
        var meeting = _meetingNow();
        if (CalendarOnly)
        {
            return meeting;
        }

        var apps = CallApps();
        foreach (var app in Last!.MicInUse)
        {
            var exe = app[(app.LastIndexOfAny(['\\', '/', '#']) + 1)..]; // a path, a NonPackaged key (C:#…#Zoom.exe) or a package family
            if (apps.Any(g => Glob.IsMatch(g, exe) || SamePackage(g, app)))
            {
                return true;
            }

            if (meeting && Browsers.Any(b => b.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A packaged app's consent key is its package family (<c>MSTeams_8wekyb3d8bbwe</c>): compare letters and digits only.</summary>
    private static bool SamePackage(string exeGlob, string app)
    {
        var name = Alnum(Path.GetFileNameWithoutExtension(exeGlob).Replace("*", "", StringComparison.Ordinal));
        var family = Alnum(app.Split('_')[0]);
        return name.Length >= 4 && family.Length > 0 && (family == name || family.EndsWith(name, StringComparison.Ordinal) || family.StartsWith(name, StringComparison.Ordinal));
    }

    private static string Alnum(string s) => new([.. s.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("in_call", InCall());
    }

    /// <summary>For the guards card (U4 has no I3 card of its own).</summary>
    public string? Notice => Enabled && CalendarOnly ? "Meeting guard is using your calendar only: the microphone check is not available" : null;

    public override IReadOnlyList<Contracts.Cards.Card> Cards() => [];
}
