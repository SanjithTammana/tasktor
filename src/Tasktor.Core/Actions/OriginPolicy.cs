using Tasktor.Contracts.Actions;

namespace Tasktor.Core.Actions;

/// <summary>What the origin policy decided for one request.</summary>
public enum PolicyVerdict
{
    Allow,
    NeedsConfirm,
    Refuse,
}

/// <summary>
/// The origin × class table of U1 D-H4 (+ U3, U4 amendments). Reads <c>cli.allow_destructive</c>. Pure.
/// </summary>
public sealed class OriginPolicy
{
    /// <summary>The CLI's <c>--yes</c> arrives as this confirm value.</summary>
    public const string CliYes = "--yes";

    public Func<bool> CliAllowDestructive { get; init; } = () => false;

    public (PolicyVerdict Verdict, string Reason) Check(ActionDef def, ActionRequest req, bool toastOffered, bool hasValidToken)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(req);
        // Surface tags (U4 H-G3) are enforced: the CLI, widget, tray and toasts reach only rows offered there.
        // The flyout, the shelf, the command line and the shell host every page (or search every row), so they reach every
        // row; rules follow the rule row below.
        var surface = req.Origin switch
        {
            Origin.Cli => Surface.Cli,
            Origin.Widget => Surface.Widget,
            Origin.Tray => Surface.Tray,
            Origin.Toast => Surface.Toast,
            _ => Surface.None,
        };
        if (surface != Surface.None && (def.Surfaces & surface) == 0)
        {
            return (PolicyVerdict.Refuse, "not_on_surface");
        }

        var sensitiveArgs = def.ConfirmArgs is { } ca && req.Args is { } a && ca.Any(k => a.TryGetValue(k, out var v) && IsTrue(v));
        var needsConfirm = def.Class == ActionClass.Destructive || sensitiveArgs;

        switch (req.Origin)
        {
            case Origin.Rule:
                if (def.Class != ActionClass.Reversible)
                {
                    return (PolicyVerdict.Refuse, "rules_reversible_only");
                }

                if (def.UserOnly)
                {
                    return (PolicyVerdict.Refuse, "user_only");
                }

                if (def.LiftsBlock)
                {
                    return (PolicyVerdict.Refuse, "rules_cannot_lift_blocks");
                }

                return (PolicyVerdict.Allow, "ok");

            case Origin.Toast:
                // A toast may run only what it offered; the click on an offered button is the confirmation.
                return toastOffered ? (PolicyVerdict.Allow, "ok") : (PolicyVerdict.Refuse, "not_offered_by_toast");

            case Origin.Cli:
                if (!needsConfirm)
                {
                    return (PolicyVerdict.Allow, "ok");
                }

                if (sensitiveArgs && def.Class != ActionClass.Destructive)
                {
                    return req.Confirm == CliYes ? (PolicyVerdict.Allow, "ok") : (PolicyVerdict.NeedsConfirm, "needs_yes");
                }

                if (!CliAllowDestructive())
                {
                    return (PolicyVerdict.Refuse, "cli_destructive_off");
                }

                return req.Confirm == CliYes ? (PolicyVerdict.Allow, "ok") : (PolicyVerdict.NeedsConfirm, "needs_yes");

            default:
                // Viewers (widget, flyout, shelf, command, tray, shell): destructive is two-step with a confirm token.
                if (!needsConfirm || hasValidToken)
                {
                    return (PolicyVerdict.Allow, "ok");
                }

                return (PolicyVerdict.NeedsConfirm, "two_step");
        }
    }

    private static bool IsTrue(string v) => v is "true" or "1" or "yes";
}
