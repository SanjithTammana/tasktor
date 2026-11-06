using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Config;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>One Module row: its description, whether it is on in config.json, and a note when that waits for a restart.</summary>
public sealed record ModuleRow(ModuleInfo Info, bool On, string? Note, bool CanTurnOnHere);

/// <summary>Settings → Rules: one rule with its switch; <see cref="Note"/> says where it comes from and when it last fired.</summary>
public sealed record RuleRow(string Id, string Title, string Note, bool On);

/// <summary>Settings → General: the command line's shortcut, and a note when Windows refused it.</summary>
public sealed record HotkeyRow(string Keys, string? Note);

/// <summary>
/// U4 D-G3 Settings → Modules: each Module with an on / off switch. Turning one on asks first with the text of
/// <see cref="Consent"/> (U1 H1: what it reads, where it is stored, what it contacts). Most Modules start and stop at
/// once; the few that apply at the next start say so on their row. Capture is turned on from its own consent screen
/// (D-C22), never from this switch. General holds the command line's shortcut (U4 OQ-G1).
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

    /// <summary>Settings → Rules (U4 D-G3: list, enable, last fired): rules that are on first, each group in the agent's order.</summary>
    public static IReadOnlyList<RuleRow> Rules(Contracts.Rules.RulesReport? report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return
        [
            .. (report?.Rules ?? []).OrderBy(r => r.On ? 0 : 1).Select(r => new RuleRow(
                r.Id,
                string.IsNullOrWhiteSpace(r.Description) ? r.Id : r.Description,
                (r.Source switch { "default" => "Shipped with Tasktor", "pack" => "From a rule pack", _ => "Yours (rules.json)" })
                    + (r.LastFired is { } at ? " · last fired " + TimeZoneInfo.ConvertTime(at, zone).ToString("H:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty),
                r.On)),
        ];
    }

    /// <summary>Opens the user's rules.json in an editor (created with a short guide if missing); a save reloads the rules.</summary>
    public static CardAction EditRules { get; } = new() { Id = "edit-rules", Label = "Edit rules.json", Action = "rules.edit" };

    /// <summary>A rule's switch: written into rules.json by the agent as a toggle (the user's text is kept).</summary>
    public static CardAction SwitchRule(RuleRow row, bool on)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CardAction
        {
            Id = "rule",
            Label = row.Title,
            Action = on ? "rule.enable" : "rule.disable",
            Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = row.Id },
        };
    }

    /// <summary>The shortcut from the snapshot's <c>state.hotkey</c>; null when the agent has no tray (nothing to set).</summary>
    public static HotkeyRow? Hotkey(JsonNode? root) =>
        root?["state"]?["hotkey"] is JsonObject h && h["keys"] is JsonValue k && k.TryGetValue<string>(out var keys)
            ? new HotkeyRow(keys, h["registered"] is JsonValue r && r.TryGetValue<bool>(out var ok) && !ok ? "Another program uses this shortcut. Pick another." : null)
            : null;

    /// <summary>The action that sets the shortcut (the agent parses and refuses bad ones in plain words).</summary>
    public static CardAction SetHotkey(string keys) => new()
    {
        Id = "hotkey",
        Label = "Change shortcut",
        Action = "settings.hotkey",
        Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["keys"] = (keys ?? string.Empty).Trim() },
    };

    /// <summary>The consent dialog's body for <paramref name="info"/>.</summary>
    public static string Consent(ModuleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return "What it reads: " + info.Reads + "\n\nWhere it is kept: " + info.Stores
            + "\n\n" + (info.Network is { } net ? "What it contacts: " + net + "." : "It makes no network connection.")
            + "\n\nYou can turn it off here at any time.";
    }
}
