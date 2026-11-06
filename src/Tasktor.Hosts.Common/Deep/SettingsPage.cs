using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Config;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>One Module row: its description, whether it is on in config.json, and a note when that waits for a restart.</summary>
public sealed record ModuleRow(ModuleInfo Info, bool On, string? Note, bool CanTurnOnHere);

/// <summary>Settings → Rules: one rule with its switch; <see cref="Note"/> says where it comes from and when it last fired.</summary>
public sealed record RuleRow(string Id, string Title, string Note, bool On);

/// <summary>Settings → Plugins: one plugin folder; <see cref="On"/> while it has the user's consent (running or failing).</summary>
public sealed record PluginRow(string Id, string Title, string Note, bool On, Contracts.Plugins.PluginInfo Info);

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

    /// <summary>Settings → Plugins (U1 D-H7): a new or changed folder is "Found, off" until the user consents.</summary>
    public static IReadOnlyList<PluginRow> Plugins(Contracts.Plugins.PluginsReport? report) =>
    [
        .. (report?.Plugins ?? []).Select(p => new PluginRow(
            p.Id,
            p.Name,
            p.State switch
            {
                Contracts.Snapshot.PluginState.Found => "Found, off",
                Contracts.Snapshot.PluginState.Error => "Error: " + (p.Error ?? "it did not run"),
                Contracts.Snapshot.PluginState.Off => "Paused",
                _ => "On",
            },
            p.State != Contracts.Snapshot.PluginState.Found,
            p)),
    ];

    /// <summary>
    /// The consent dialog's body (U1 D-H7): name, author, what it reads, what it contacts, its actions and the exact
    /// command line, then the two limits said plainly.
    /// </summary>
    public static string PluginConsent(Contracts.Plugins.PluginInfo p)
    {
        ArgumentNullException.ThrowIfNull(p);
        static string List(IReadOnlyList<string> items) => items.Count == 0 ? "nothing" : string.Join(", ", items);
        return (p.Author is { Length: > 0 } a ? "From: " + a + "\n\n" : string.Empty)
            + "What it reads: " + List(p.Reads)
            + "\n\n" + (p.Network.Count == 0 ? "It declares no network use." : "What it contacts: " + List(p.Network) + ".")
            + "\n\nIts actions: " + List(p.Actions)
            + "\n\nIt runs: " + (p.CommandLine ?? "(unknown)")
            + "\n\nTasktor cannot block a plugin's network use (that needs admin firewall rules), and a plugin runs with your rights, like any program you run. If its files change, Tasktor asks again.";
    }

    /// <summary>A plugin's switch: on records the consent (hashes pinned); off drops it, so turning it on again asks again.</summary>
    public static CardAction SwitchPlugin(PluginRow row, bool on)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CardAction
        {
            Id = "plugin",
            Label = row.Title,
            Action = on ? "plugin.enable" : "plugin.disable",
            Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["plugin"] = row.Id },
        };
    }

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
