using Tasktor.Contracts.Config;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>One Module row: its description, whether it is on in config.json, and a note when that waits for a restart.</summary>
public sealed record ModuleRow(ModuleInfo Info, bool On, string? Note, bool CanTurnOnHere);

/// <summary>
/// U4 D-G3 Settings → Modules: each Module with an on / off switch. Turning one on asks first with the text of
/// <see cref="Consent"/> (U1 H1: what it reads, where it is stored, what it contacts). A change applies when Tasktor
/// restarts, and the row says so. Capture is turned on from its own consent screen (D-C22), never from this switch.
/// </summary>
public static class SettingsPage
{
    public static IReadOnlyList<ModuleRow> Modules(ModulesReport? report)
    {
        var states = (report?.Modules ?? []).ToDictionary(m => m.Id, StringComparer.Ordinal);
        return
        [
            .. ModuleCatalog.All.Select(info =>
            {
                var s = states.GetValueOrDefault(info.Id);
                var on = s?.On == true;
                var note = s is null ? null
                    : on && !s.Running ? "On after Tasktor restarts"
                    : !on && s.Running ? "Off after Tasktor restarts"
                    : null;
                return new ModuleRow(info, on, note, CanTurnOnHere: info.Id != "capture");
            }),
        ];
    }

    /// <summary>The consent dialog's body for <paramref name="info"/>.</summary>
    public static string Consent(ModuleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return "What it reads: " + info.Reads + "\n\nWhere it is kept: " + info.Stores
            + "\n\n" + (info.Network is { } net ? "What it contacts: " + net + "." : "It makes no network connection.")
            + "\n\nYou can turn it off here at any time.";
    }
}
