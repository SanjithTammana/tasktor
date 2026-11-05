using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Rules;
using Tasktor.Core.Windows;

namespace Tasktor.Core.Features.Windows;

/// <summary>F7: classify the foreground on events, never on a polling timer.</summary>
public sealed class FullscreenSource(FeatureContext context, IEventSource<WindowEvent>? events) : Feature(context)
{
    private long _revision;
    private string _value = FullscreenClassifier.None;

    public override string Id => "fullscreen";

    public override FeatureTier Tier => FeatureTier.Core;

    public Func<long, WindowInfo?> Window { get; set; } = _ => null;

    public Func<IReadOnlyList<MonitorInfo>> Monitors { get; set; } = () => [];

    public Func<NotificationState> Notification { get; set; } = () => NotificationState.AcceptsNotifications;

    protected override void OnEnable()
    {
        Context.UpdateState(s => s with { Fullscreen = Fullscreen.None });
        events?.Start(e => Context.Loop.Post(() => OnEvent(e)));
    }

    protected override void OnDisable()
    {
        _revision++;
        events?.Stop();
    }

    public void OnEvent(WindowEvent e)
    {
        if (!Enabled || e.Kind != WindowEventKind.Foreground) return;
        var revision = ++_revision;
        Context.OffLoop(() =>
        {
            var monitors = Monitors();
            var value = monitors.Count == 0 ? FullscreenClassifier.None : FullscreenClassifier.Classify(Notification(), Window(e.Hwnd), new MonitorMap(monitors));
            Context.Loop.Post(() =>
            {
                if (!Enabled || revision != _revision || value == _value) return;
                _value = value;
                var state = value switch
                {
                    FullscreenClassifier.Borderless => Fullscreen.Borderless,
                    FullscreenClassifier.Exclusive => Fullscreen.Exclusive,
                    FullscreenClassifier.Presentation => Fullscreen.Presentation,
                    _ => Fullscreen.None,
                };
                Context.UpdateState(s => s with { Fullscreen = state });
                Context.RulesDirty();
            });
        });
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now) => inputs.State("fullscreen", _value);

    public override IReadOnlyList<Card> Cards() => [];
}
