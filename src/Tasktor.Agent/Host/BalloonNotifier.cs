using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features;

namespace Tasktor.Agent.Host;

/// <summary>A balloon on the tray icon (Windows shows it as a notification): title, body, icon, and the tag it stands for.</summary>
internal sealed record BalloonView(string Title, string Body, BalloonIcon Icon, string Tag);

/// <summary>NOTIFYICONDATA's NIIF_INFO / NIIF_WARNING / NIIF_ERROR.</summary>
internal enum BalloonIcon
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// U4 D-G7's fallback (and today's notifier on the portable build): toasts as balloons on the agent's tray icon, which
/// Windows 10 / 11 show as notifications and file in the notification centre; quiet hours and Do Not Disturb apply.
/// One tag at a time: a new toast replaces the shown one, and <see cref="Withdraw"/> clears it only if it is still the
/// one showing. Balloons cannot carry buttons, so their labels become a hint and a click opens the shelf, where the same
/// actions live (with the inline confirm). Loop-only.
/// </summary>
internal sealed class BalloonNotifier(Action<BalloonView?> show) : INotifier
{
    public const int MaxTitle = 63;   // szInfoTitle[64]
    public const int MaxBody = 255;   // szInfo[256]

    private string? _shownTag;

    public string? ShownTag => _shownTag;

    public void Show(ToastRequest toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        _shownTag = toast.Tag;
        show(Plan(toast));
    }

    public void Withdraw(string tag)
    {
        if (_shownTag is not null && _shownTag == tag)
        {
            _shownTag = null;
            show(null);
        }
    }

    public static BalloonView Plan(ToastRequest toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        var body = toast.Body?.Trim() ?? string.Empty;
        if (toast.Buttons.Count > 0)
        {
            var hint = "Click to choose: " + string.Join(" / ", toast.Buttons.Select(b => b.Label));
            body = body.Length == 0 ? hint : body + "\n" + hint;
        }

        var icon = toast.Severity switch
        {
            Severity.Critical => BalloonIcon.Error,
            Severity.Warn => BalloonIcon.Warning,
            _ => BalloonIcon.Info,
        };
        return new BalloonView(Cut(toast.Title, MaxTitle), Cut(body, MaxBody), icon, toast.Tag);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// The agent is built before the tray exists, and features keep the notifier they were given; this one forwards to
/// whichever notifier is current (nothing until the tray is up).
/// </summary>
internal sealed class SwitchableNotifier : INotifier
{
    public INotifier Target { get; set; } = NullNotifier.Instance;

    public void Show(ToastRequest toast) => Target.Show(toast);

    public void Withdraw(string tag) => Target.Withdraw(tag);
}
