# Rules (grammar v1)

Tasktor's alerts and guards are rules: **when** a condition has held **for** a while, **then** do something, at most once per
**cooldown**, and with **undo** reverse it when the condition clears. The shipped rules live read-only in the install
folder (`rules.default.json`); yours go in `%LOCALAPPDATA%\Tasktor\rules.json` (JSON with comments, schema
`schemas/tasktor.rules-1.json`). The file is checked on load and on every change; a bad rule is turned off with the line
and the reason, and the others keep working.

## Overriding the shipped rules

- A rule in your file with the same `id` **replaces** the shipped one.
- `{ "id": "cpu-saturation", "enabled": true }` turns a shipped-off rule on without restating it.
- `{ "id": "ram-pressure", "enabled": false }` turns one off.
- `groups` merge by name: your list replaces the shipped list for that group. Guards act only on groups you fill in
  (`meeting_guarded`, `battery_guarded`; empty by default).

## A rule

```jsonc
{ "id": "battery-restrain",
  "description": "On battery, opted-in apps run in efficiency mode",
  "when": { "all": [ { "state": "on_battery" }, { "group": "battery_guarded", "running": true } ] },
  "for": "30s", "cooldown": "30m",
  "then": [ { "efficiency": true, "target": "group:battery_guarded" } ],
  "undo": true }
```

Durations: `30s`, `5m`, `2h`, `1d`.

## Conditions (exactly one kind per object)

| Kind | Keys | Meaning |
|---|---|---|
| metric | `metric`, `instance`?, `above` / `below` | a number such as `system.cpu_pct`, `disk.free_pct` (instance `C:`), `battery.pct`, `connectivity.drops_10m` |
| learned | `learned: { stat, key, fallback, clamp?, multiplier? }` | a learned threshold with a fallback (built-in rules only) |
| group | `group`, `count_above` / `busy_count_above` / `running` | processes of a named group |
| process | `process` (glob), `cpu_above` / `age_above` / `silent_for` | one process by name |
| state | `state`, `is`? | `on_battery`, `low_memory`, `in_call`, `meeting_now`, `event_mode`, `focus_block`, `offline`, `captive_portal`, `fullscreen` (`is`), `tracking_paused`, `agent_needs_permission` … |
| event | `event`, filters `folder` / `glob` / `app` / `title` / `agent` | `file.created`, `window.missing`, `agent.state`, `meeting.soon`, `deadline.soon`, `focus.block_end`, `job.done`, `power.unplugged` … |
| combinators | `all`, `any`, `not` | |

The full list of names is `src/Tasktor.Core/Rules/RuleCatalog.cs`; new names are catalog rows, not grammar changes.

## Actions

Rules may only call **reversible** actions; destructive ones never run from a rule.

| Action | Example |
|---|---|
| `toast` (≤ 3 `buttons`), `severity` | `{ "toast": "Memory is low: {offender}", "buttons": ["app.close", "open:core.apps", "snooze:1h"] }` |
| `badge`, `card` (pin) | `{ "badge": "{count}" }` |
| `pause` / `resume` + `mode` | `refits`, `tracking`, `capture`, `plugin:<id>`, `job:<id\|group>` with `polite` / `freeze` |
| `set_priority`, `efficiency`, `cap_cores` | with `target`: `group:<name>`, `process:<glob>`, `pid:<n>` |
| `plugin_action` | a plugin's own declared action |

Toast buttons: an action id the toast may carry (`job.log`, `agent.show`, `url.open`, `focus.next` …), `open:<card>`,
`snooze:<duration>`, or `run:<command>`. **`run` exists only as a button**: a command declared in the same rule's
`commands` (argv, no shell), started only when you click it:

```jsonc
{ "id": "new-pdf",
  "when": { "event": "file.created", "folder": "{Downloads}", "glob": "*.pdf" },
  "then": [ { "toast": "New PDF: {file}", "buttons": ["run:convert"] } ],
  "commands": { "convert": ["{UserProfile}/tools/convert.exe", "{path}"] } }
```

Placeholders: `{value}`, `{instance}`, `{count}`, `{process}`, `{job}`, `{file}`, `{path}`, `{title}`, `{agent}`,
`{folder}` and Known Folders (`{Downloads}`, `{Documents}`, `{UserProfile}`, `{LocalAppData}`, `{Temp}` …). Shipped rules
never contain literal user paths.

On the guards card, **Undo** reverses an active guard now; it stays off until its condition clears.
