using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;

namespace Tasktor.Agent.Tests;

/// <summary>U4 D-G7 fallback: toasts as tray balloons.</summary>
public sealed class BalloonNotifierTests
{
    private readonly List<BalloonView?> _shown = [];

    private BalloonNotifier Notifier() => new(v => _shown.Add(v));

    private static ToastRequest Toast(string tag, string title = "Disk C: almost full", string? body = "12 GB left", Severity severity = Severity.Warn, params string[] buttons) => new()
    {
        Tag = tag,
        Title = title,
        Body = body,
        Severity = severity,
        Buttons = [.. buttons.Select(b => new ToastButton { Label = b, Request = new ActionRequest { Action = "alert.snooze", Origin = Origin.Toast } })],
    };

    [Fact]
    public void Severity_picks_the_icon_and_buttons_become_a_hint()
    {
        var v = BalloonNotifier.Plan(Toast("disk", buttons: ["Open Storage", "Snooze"]));
        Assert.Equal(BalloonIcon.Warning, v.Icon);
        Assert.Equal("12 GB left\nClick to choose: Open Storage / Snooze", v.Body);
        Assert.Equal(BalloonIcon.Error, BalloonNotifier.Plan(Toast("x", severity: Severity.Critical)).Icon);
        Assert.Equal(BalloonIcon.Info, BalloonNotifier.Plan(Toast("x", severity: Severity.Normal)).Icon);
    }

    [Fact]
    public void Title_and_body_fit_the_shell_limits()
    {
        var v = BalloonNotifier.Plan(Toast("x", new string('t', 100), new string('b', 400)));
        Assert.Equal(BalloonNotifier.MaxTitle, v.Title.Length);
        Assert.Equal(BalloonNotifier.MaxBody, v.Body.Length);
        Assert.EndsWith("…", v.Body, StringComparison.Ordinal);
        Assert.Equal(string.Empty, BalloonNotifier.Plan(Toast("x", body: null)).Body);
    }

    [Fact]
    public void A_new_toast_replaces_the_shown_one_and_withdraw_only_clears_its_own_tag()
    {
        var n = Notifier();
        n.Show(Toast("battery"));
        n.Show(Toast("disk"));
        n.Withdraw("battery"); // no longer showing: nothing happens
        Assert.Equal(2, _shown.Count);
        n.Withdraw("disk");
        Assert.Null(_shown[^1]);
        Assert.Null(n.ShownTag);
        n.Withdraw("disk"); // already gone
        Assert.Equal(3, _shown.Count);
    }

    [Fact]
    public void The_switchable_notifier_forwards_once_a_target_is_set()
    {
        var s = new SwitchableNotifier();
        s.Show(Toast("early")); // before the tray: dropped
        s.Target = Notifier();
        s.Show(Toast("late"));
        Assert.Equal("late", Assert.Single(_shown)!.Tag);
    }
}
