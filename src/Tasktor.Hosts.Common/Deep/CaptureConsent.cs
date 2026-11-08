using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// C D-C22: capture's own consent screen (the signed board's copy). Turning capture on is possible only from here: the
/// request carries <c>screen=capture</c> and the user's acknowledgement, and the agent refuses without both. The
/// confirm button stays disabled until the acknowledgement is ticked; "Not now" is the default.
/// </summary>
public static class CaptureConsent
{
    public const string Title = "Turn on deep capture?";

    public const string Lead = "Tasktor will record what you type and the text on your screen, so it can learn how you work and let you search it later.";

    public const string Acknowledgement = "I understand Tasktor records what I type, except in password and secure fields.";

    public const string Confirm = "Turn on capture";

    public const string Cancel = "Not now";

    /// <summary>The four facts, in the board's order (D-C22's never-recorded list, storage, indicator, controls).</summary>
    public static IReadOnlyList<(string Heading, string Text)> Facts { get; } =
    [
        ("Never recorded", "password and secure fields, the secure desktop, private windows, other Windows users, and the apps and sites you exclude"),
        ("Stays on this PC", "encrypted for your Windows account, deleted after 7 days, never uploaded"),
        ("Always visible", "a red ring on the tray icon and a line along the top of the screen"),
        ("Yours to stop", "pause any time, block an app, site or tab, forget the last hour"),
    ];

    public static bool CanConfirm(bool acknowledged) => acknowledged;

    /// <summary>The <c>module.enable</c> request this screen sends once the user has ticked the acknowledgement.</summary>
    public static CardAction Request(bool acknowledged)
    {
        if (!acknowledged)
        {
            throw new InvalidOperationException("Capture is turned on only after the acknowledgement is ticked.");
        }

        return new CardAction
        {
            Id = "capture.consent",
            Label = Confirm,
            Action = "module.enable",
            Target = new Dictionary<string, string> { ["module"] = "capture" },
            Args = new Dictionary<string, string> { ["screen"] = "capture", ["acknowledged"] = "true" },
        };
    }
}
