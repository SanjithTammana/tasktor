# Tasktor: U1. Contracts and platform (feature spec)

**Status:** **signed**. D-H1–D-H13 decided: D-H12 is unsigned releases with T1 included; D-H13 is Apache-2.0. Open items are probes only (OQ-H1–OQ-H6). The tags show who drafted each one.

**What this covers.** U1 replaces section H's 2026-11-03 draft, and also takes in:
- D1 (status and the local API);
- D5 (`tasktor summary`);
- the file format for I1's agent hook.


**Fixed inputs (signed, not relitigated):**
- **A:**
  - D-A3 budgets.
  - D-A5: four shapes, seven interfaces, `ICardProvider` blocks plus hints, `IBlockRenderer` on the viewer side (owned by G), and the one-logic-thread rule in §7.
  - D-A6 slot map.
  - `IProcessControl` with B's and C's members.
  - `SnapshotPublisher`, `SelfMonitor`.
- **B:**
  - D-B6: the progress file is a public contract.
  - D-B9 / D-B10: learned state is numbers only and can be forgotten.
  - D-B11.
  - B's H asks: the storage files, the toast-button conflict, the grammar additions, the budget share, and export without learned state.
- **C:**
  - D-C7: T0 adds at most 2 MB.
  - D-C9: `winsqlite3`.
  - D-C11: embeddable Python 3.13 in a Job Object.
  - D-C21: the capture process.
  - D-C22: privacy tiers and `forget --all`.
  - C's H asks: hosts, SQLCipher, signing, the network CI check, H14, and the `IProcessControl` review.
- **Stack:** C# everywhere, the agent is Native AOT, Python is for learning.

Conventions:
- **[verified: source]** = checked this session or carried from a verified draft.
- **[unverified]** = still a claim, checked in the named phase.
- **[estimate]** = a reasoned number, to be measured.

## Why U1 exists (the failure it prevents)
Before this unit, three drafts each had their own idea of what a card is:
- D restated the snapshot.
- G invented a card model.
- H invented a third card shape for plugins.

On top of that, B wanted CLI job actions, while D called the API "read-only". If those drafts ship, every viewer, plugin and script codes against a different shape, and changing one breaks the others.

U1 fixes **three contracts** once, as types, so every other unit cites them:
1. **Snapshot + actions.**
2. **Rules.**
3. **Plugins.**

## Features (U1)
| # | Feature | Tier | Phase | From |
|---|---|---|---|---|
| H1 | First run asks for nothing; each permission is asked when its feature is turned on | Core | A0 | H |
| H2 | Local-only, no account, no telemetry, as a CI-tested property | Core | A0 | H |
| H3 | Store layout, settings files, migration, export / import / reset, `forget` family | Core | A0 | H + B + C |
| H4 | Viewers follow the Windows theme, accent, high contrast and animation settings (G designs the look) | Core | W1 | H |
| H5 | Budgets for every Tasktor process, one table, tested in CI | Core | A0 | A + B + C |
| H6 | Local log without personal data (`Sensitive<T>`), verbose mode, log bundle | Core | A0 | H |
| H7 | Tests + CI: Linux for Core, Windows for OS code and packaging, manual checks list | Core | A0 | H |
| H8 | Two builds: MSIX and portable zip; Python bundled | Core | V1 | H + C |
| H9 | Repo docs + license | Core | V1 | H |
| H10 | Rules file: grammar v1 | Core | W1 | H + B |
| H11 | Plugin contract v1: manifest, blocks output, limits, consent | Core | P1 | H |
| H12 | Distribution: GitHub Releases first | Core | V1 | H |
| H13 | Update check: notify only, off by default | later | — | H |
| H14 | Self-use counts (moved from C13) | Core | A1 | C |
| D1 | `tasktor status` + local API: the snapshot over a file and a pipe, plus the guarded action verb | Core | A0 | D + B |
| D5 | `tasktor summary`: numbers-only workflow summary | Core | later | D + C |
| H15 | Process topology and supervision: which processes exist, who starts them, how they stop | Core | A0 | new (C's hosts) |

## Cross-cutting decisions

### D-H1 One schema source: contracts are C# records
- Every public contract is a set of records in `Tasktor.Contracts`, serialised by source-generated `System.Text.Json`. Source generation is the Native AOT path; the reflection serialiser is not **[verified: learn.microsoft.com native-aot + system-text-json source-generation]**.
- CI generates **JSON Schema** files from those records into `schemas/`:
  - Python (the worker, the plugin SDK) and any outside script validate against those files.
  - Editors (VS Code) give autocomplete for `rules.json` and `plugin.json` from the `$schema` key.
- **Public contracts** are documented in the repo and carry a stability promise. Majors live in the id; additive changes keep the major; readers ignore unknown fields. A breaking change bumps the major, and the CLI keeps `--schema <previous>` for one release.
  | Id | What | Owner |
  |---|---|---|
  | `tasktor.snapshot/1` | state + cards (D-H2) | U1 |
  | `tasktor.action/1` | action request / result (D-H4) | U1 |
  | `tasktor.rules/1` | `rules.json` (D-H6) | U1 |
  | `tasktor.plugin/1` | `plugin.json` + plugin output (D-H7) | U1 |
  | `tasktor.job/1` | progress file `jobs\<id>.json` | B (D-B6), schema hosted here |
  | `tasktor.agent/1` | agent hook file `agents\<session>.json` (I1) | U1 format, U2 behaviour |
- **Internal contracts** carry no promise and can change in any release, because Tasktor ships both ends:
  - the pipe framing;
  - the model file contract (D-C10);
  - the training extract (D-C2);
  - the `SendControl` messages;
  - the native-messaging frames.
- **Why:** the alternative, a hand-written schema per contract plus hand-written C# and Python models, triples the places where a field can drift.

### D-H2 Snapshot = state + cards bound to state
**The problem.** Scripts and agents (D1) want **data**: numbers and rows with stable names. Viewers want **cards**: A's blocks plus hints.

**The approaches compared.**
| Approach | Scripts | Viewers | Cost | Verdict |
|---|---|---|---|---|
| a. Cards only | parse blocks to find "CPU %": fragile | ✓ | small | ✗ |
| b. State only | ✓ | every viewer rebuilds cards from data, which is G doing A's job again | small | ✗ |
| c. Both, values copied into cards | ✓ | ✓ | every value twice; card JSON changes every sample | ✗ |
| **d. Both, with cards bound to state** | ✓ | ✓ | each value once; card layouts change rarely | ** chosen** |

**What (d) means.**
- A card block holds either a literal value or a **binding**: `{"bind": "/state/system/cpu_pct"}` (a JSON Pointer into `state`).
- Renderers resolve the bindings. This is the template / data split the widget host already uses: Adaptive Cards template + data **[verified: G draft, implement-widget-provider-win32]**.
- `cards` change only when a card's structure changes, such as a card added or rows reordered. `state` changes per sample.
- **Plugins** send literal values only: they have no `state`, so their output stays one simple JSON document (D-H7).

**Shape (fields fixed in WS0):**
```json
{ "schema": "tasktor.snapshot/1", "generated_at": "…", "seq": 812,
  "agent":  { "version": "…", "running": true, "paused": { "tracking": false, "capture": false },
              "self": { "private_mb": 11.8, "cpu_pct": 0.04, "wakeups_per_min": 1.2 } },
  "state":  { "system": { "cpu_pct": 34, "ram_used_pct": 71, "commit_pct": 64, "gpu": [ … ], "disks": [ … ], "net": { … }, "battery": null },
              "apps":   [ { "app_id": "…", "name": "…", "cpu_pct": …, "private_mb": …, "gpu_pct": … } ],   // top N; the full table is pulled
              "jobs":   [ /* JobRow, B */ ],
              "workflow": { "id": "…", "name": "…", "confidence": 0.71, "source": "learned|default", "level": "workflow|activity|app" },
              "alerts": [ { "rule": "disk-low", "severity": "warn", "card": "core.disk", "since": "…" } ],
              "plugins": [ { "id": "…", "state": "ok|error|off", "updated_at": "…" } ] },
  "cards":  [ { "id": "core.load", "source": { "kind": "core" }, "title": "System", "icon": "cpu",
                "severity": { "bind": "/state/alerts/…" },
                "blocks": [ { "kind": "Metric", "label": "CPU", "value": { "bind": "/state/system/cpu_pct" }, "unit": "%", "priority": 1 },
                            { "kind": "Sparkline", "series": "system.cpu_pct", "priority": 3 } ],
                "actions": [ { "id": "open", "label": "Open", "priority": 1 } ],
                "link": "tasktor://open?card=core.load", "stale_after_s": 120 } ] }
```
- **Series** (sparklines) are not in the snapshot file. Viewers pull them over the pipe while open. This keeps the file small and the D-A1 hour ring in memory only.
- **Size:** `state` ≤ 32 KB, `cards` ≤ 32 KB **[estimate, A0]**. The full process table (~600 rows) is pulled only while the flyout table is open (A's time/space table).

### D-H3 Transport: one file, one pipe, current user only
**`snapshot.json`.**
- Rewritten atomically: write a temp file, then `ReplaceFile`.
- At most one write per second while values change, and none while nothing changes.
- Readers that cannot use a pipe (the plugins, a quick script, an agent with only file tools) read it.
- It inherits the profile ACL.

**Named pipe `\\.\pipe\tasktor-<hash of user SID>`.**
- The DACL is the current user only. A remote client is rejected (`PIPE_REJECT_REMOTE_CLIENTS`) **[verified: CreateNamedPipe doc]**.
- Messages are JSON lines (internal framing).

| Message | From → to | Use |
|---|---|---|
| `hello {client, contracts[]}` → `welcome {agent, contracts[]}` | client → agent | version handshake; a client asking for an unknown major gets an error, not a guess |
| `subscribe {state paths[], cards?}` | viewer → agent | the agent pushes `delta {seq, ops[]}` (JSON Patch subset: replace / add / remove), coalesced to at most one per 500 ms per client |
| `pull {table, columns[], top?}` | viewer → agent | full tables (processes, listeners, folder tree), series, the Learned view (B6) |
| `act {request}` → `result` | any → agent | D-H4 |
| `lease {source, columns, interval}` | viewer → agent | a viewer's `DemandLease` (D-A5): the agent samples at 1–2 s only while a viewer holds one; the lease dies with the pipe connection |

- **No TCP listener, ever.** This is also an H2 test.
- The pipe accepts at most 16 clients, which is plenty for viewers + the CLI + plugins.

### D-H4 Actions: one verb, one router, a policy per origin
Every action in Tasktor becomes an `ActionRequest` that passes through **one** `ActionRouter` on the logic thread:
- a widget button, a flyout button, a tray menu item, a toast button;
- a CLI command, a rule's `then`, a plugin card button.

```json
{ "schema": "tasktor.action/1", "action": "job.pause", "target": { "job": "j-41" }, "args": { "mode": "polite" },
  "origin": "cli", "confirm": null }
```
**The router's steps:**
1. Look the action up in the **action catalog**, a closed list in Contracts. Unknown actions are rejected.
2. Check the **origin policy** (table below).
3. For a process action, run A3's `GuardChain`.
4. If the action is destructive and has no confirm token, return `{needs_confirm, token, summary}`. The token is valid for 30 s and for this request only.
5. Hand the action to its handler: `IProcessControl`, a feature's handler, or the plugin host. It runs off-loop (D-A5 §7) and the result comes back as an event.
6. Log it (H6: time, action, target exe name, origin, result).

**Action classes:**
- **Read:** status, pull, summary.
- **Reversible:** lower or restore priority, efficiency mode, pause or resume a job (polite or freeze), snooze an alert, pause or resume tracking / capture / refits / a plugin, pin a card.
- **Destructive:** close, end, end tree, and anything that runs a command.

| Origin | Read | Reversible | Destructive |
|---|---|---|---|
| Viewers (widget, flyout, tray) | ✓ | ✓ | ✓ two-step (the widget opens the flyout with the confirm step armed, per G1) |
| Toast button | ✓ | ✓ | ✓ only actions the toast itself offered |
| CLI | ✓ | ✓ | only with `--yes` **and** setting `cli.allow_destructive = true` (off by default) |
| Rules | — | ✓ (and `undo` must reverse them) | ✗ never; `run` only from a toast button click (D-H6) |
| Plugin card buttons | — | the plugin's **own** declared actions only | ✗ never touches processes |

- **Why off by default for the CLI:** an AI agent with shell access can call `tasktor`. Ending a process stays a human decision unless the user opts in.
- **CLI verbs (thin pipe client):**
  - `tasktor status [--json] [--cards]` (D1);
  - `tasktor job lower|restore|pause|resume|end <id>`;
  - `tasktor proc lower|restore|close|end <pid>`;
  - `tasktor pause|resume tracking|capture|refits`;
  - `tasktor summary` (D5);
  - `tasktor forget …` (D-H8);
  - `tasktor settings …`;
  - `tasktor log …`;
  - `tasktor job …` progress-file writes (B4.7, unchanged).
- **Exit codes:** 0 ok; 2 refused by policy or guard; 3 agent not running (prints `{"agent":{"running":false}}` with `--json`); 4 needs confirm.

### D-H5 Settings and rules are JSON, not TOML
| Criterion | TOML (old H draft) | **JSON with comments ()** |
|---|---|---|
| Native AOT parser | needs a third-party library (Tomlyn); whether its model mapping is trimming-safe is **[unverified]** | `System.Text.Json` source generation, already in the agent; `JsonCommentHandling.Skip` + `AllowTrailingCommas` accept comments and trailing commas **[verified: System.Text.Json docs]** |
| One parser in the agent | ✗ two (TOML + JSON) | ✓ one |
| Editing help | TOML plugin, no schema | the `$schema` key gives autocomplete + validation in VS Code from D-H1's generated schemas |
| Python side | `tomllib` (stdlib) | `json` (stdlib); comments stripped by a 10-line helper |
| Hand-editing comfort | better | good enough with comments allowed |

- `config.json` and `rules.json` both use this format, which matches A and the Frame (`rules.json`).
- Every file carries `schema_version`. The agent migrates forward on start and keeps a `.bak` of the old version.

### D-H6 Rules grammar v1
```jsonc
{ "$schema": "https://…/schemas/tasktor.rules-1.json", "schema_version": 1,
  "groups": { "agents": ["claude.exe", "codex.exe"], "builds": ["msbuild.exe", "node.exe"] },
  "rules": [
    { "id": "disk-low", "when": { "metric": "disk.free_pct", "instance": "C:", "below": 10 },
      "for": "5m", "cooldown": "6h",
      "then": [ { "toast": "Drive {instance} is down to {value}% free", "buttons": ["open:core.disk"] } ] },
    { "id": "job-silent", "builtin": true,                      // B2, adaptive
      "when": { "learned": { "stat": "silence", "key": "job.kind", "fallback": "10m", "clamp": ["2m", "60m"] } },
      "then": [ { "toast": "{job} has been quiet for {value}", "buttons": ["open:core.jobs", "job.log", "rule.its_fine"] } ] },
    { "id": "battery-restrain",                                  // I4
      "when": { "all": [ { "state": "on_battery" }, { "group": "builds", "running": true } ] },
      "then": [ { "set_priority": "below_normal", "target": "group:builds" }, { "pause": "refits" } ],
      "undo": true },
    { "id": "new-pdf",                                           // I12
      "when": { "event": "file.created", "folder": "{Downloads}", "glob": "*.pdf" },
      "then": [ { "toast": "New PDF: {file}", "buttons": ["run:convert"] } ],
      "commands": { "convert": ["{UserProfile}/tools/convert.exe", "{path}"] } } ] }
```
**Conditions:**
- `metric` + `above` / `below`: any `MetricId` in the catalog.
- `learned`: A's `LearnedThreshold`, which has a fallback, a clamp, and `source` reported as learned or default.
- `group` + `count_above` / `running`.
- `process` (name or glob) + `cpu_above` / `age_above` / `silent_for`.
- Combinators: `all`, `any`, `not`.
- Time: `for` (A's `HoldFor`), `cooldown`.

**Actions** (the D-H4 classes apply):
- `toast` with **up to 3 buttons**. This resolves B's conflict: Windows allows "up to 5 buttons (including context menu items)" **[verified: B, app-notifications docs]**, and 3 leaves room for the system's own.
- `badge`, `card` (pin).
- `pause` / `resume` with target `refits` | `tracking` | `capture` | `plugin:<id>` | `job:<id|group>`, plus `mode` = `polite` | `freeze` for jobs (B3.7).
- `plugin_action`: the plugin's own declared action.
- `run`: a command declared in the same rule's `commands`, offered only as a toast button.
- **Built-in rules only:** `use_fallback` / `relearn` (B's guardrail actions).


**Placeholders:** `{value}`, `{instance}`, `{count}`, `{process}`, `{job}`, `{file}`, `{path}`, and Known Folder names such as `{Downloads}` or `{UserProfile}` (never literal paths in shipped rules).

**Safety:**
- The file is schema-checked on load and on every change (hot reload).
- A bad rule is disabled and listed on an error card with its line number; the rest keep running.
- Evaluation is pure: snapshot + events in, action requests out. This makes it Linux-testable.
- Shipped default rules live read-only in the install dir. A user rule with the same `id` overrides one; `"enabled": false` turns one off.

### D-H7 Plugins emit A's blocks, a fixed subset
**`plugin.json` (contract 1).** The example is a local one, so no feature we cut is used as the example:
```json
{ "$schema": "https://…/schemas/tasktor.plugin-1.json", "contract": 1,
  "id": "example.todo-count", "name": "TODO count", "version": "0.1.0", "author": "Jane Doe",
  "description": "Counts open TODO lines in one text file.",
  "command": ["python", "main.py"], "interval": "5m", "timeout": "10s",
  "reads": ["The file you choose (setting)"], "network": [],
  "settings": [ { "key": "file", "label": "File", "type": "path" } ],
  "actions":  [ { "id": "refresh", "label": "Refresh", "class": "reversible" } ],
  "min_tasktor": "1.0" }
```
**Invocation.**
- Tasktor runs `command` with the plugin dir as the working directory.
- stdin gets one JSON object: `{contract, call: "render"|"action", action?, args?, settings, snapshot_path}`.
- `PYTHONPATH` / `NODE_PATH` are not inherited.
- The bundled Python is offered as `{python}` in `command` for plugins that want it.

**Output** (stdout, one JSON document, ≤ 64 KB):
```json
{ "contract": 1, "cards": [ {
    "id": "main", "title": "TODOs", "icon": "list", "severity": "normal",
    "blocks": [ { "kind": "Metric", "label": "Open", "value": 12, "priority": 1 },
                { "kind": "List", "items": ["Fix login", "Write docs"], "max": 5, "priority": 2 } ],
    "actions": [ { "id": "refresh", "label": "Refresh", "priority": 1 } ] } ] }
```
**The subset plugins may use:**
- `Metric`, `Sparkline` (literal points, ≤ 60), `Bar`, `Gauge`;
- `Table` (≤ 12 rows), `List`, `Progress`;
- `Badge`, `Countdown`, `Actions`.

Not allowed: `Tree` (too large to send) and `Alert` (toasts go through rules: a plugin card can raise severity, and the user's rule decides whether that becomes a toast).

There are ≤ 3 cards and ≤ 3 actions per card. The host converts the output into `ICardProvider` cards, so viewers cannot tell a plugin card from a built-in one, apart from the `source` badge.

**Limits:**
- Runs as the user, at BelowNormal, in a kill-on-close Job Object (256 MB memory cap, no breakaway).
- `interval` ≥ 30 s or `"manual"`; `timeout` ≤ 60 s; killed at the timeout.
- One run at a time per plugin; the last 4 KB of stderr is kept for the error card.
- After 3 failures in a row the plugin turns off and shows an error card.

**Consent.**
- A new folder shows as "Found, off". Enabling it opens one dialog that shows the name, author, `reads`, `network`, actions and the **exact command line**.
- On OK, Tasktor pins the SHA-256 of `plugin.json` and of the command's target file. Any change asks again.
- A **pack** is a folder of plugins plus `pack.json`, and gets one dialog for all of them.

**What plugins never get:**
- the event store, the capture vault, the index;
- other plugins' data;
- Tasktor's settings or the install key;
- any process action.

They may read `snapshot.json` (`state` + `cards`) like any local script.

**Limits we cannot enforce (said plainly in the consent dialog and the docs):**
- `network` is a declaration; Tasktor cannot block a plugin's network, because that needs admin firewall rules.
- A plugin runs with the user's rights, like any program they run.

### D-H8 Store root, layout, and the `forget` family
**Root.**
- `%LOCALAPPDATA%\Tasktor\`, resolved through `FOLDERID_LocalAppData`, never as a literal path.
- It is not Roaming, because roaming profiles can sync `%APPDATA%` off the PC (privacy rule 1).

**MSIX.**
- New AppData files of a packaged full-trust app go to a private per-package location **[verified: desktop-to-uwp-behind-the-scenes]**. That would hide `snapshot.json` from the CLI and plugins, and split data between the two builds.
- The fix: `virtualization:ExcludedDirectories` = `$(KnownFolder:LocalAppData)\Tasktor`, with the `unvirtualizedResources` capability (Windows 11+) **[verified: flexible-virtualization]**.
- Whether the Store accepts that capability is **[unverified, V1]**. Fallback: MSIX-only readers use the pipe, and the file lives in the package location.

**Portable:** `--data-dir` or a `portable.flag` file next to the exe puts data beside the exe.

```
%LOCALAPPDATA%\Tasktor\
  config.json  rules.json  snapshot.json                     user settings · user rules · public snapshot (D1)
  state\       install.key (DPAPI, current user)  consents.json  pause.json  job-kinds.json (B6)
               frozen.json (B3.7 recovery)  selfuse.json (H14)  window.json
  jobs\        <id>.json (progress, B4)  <id>.control.json (polite pause, B)
  agents\      <session>.json (I1 hook files)
  track\       events.db (30 days)  episodes.db (kept)  extract\ (training extract)  models\   (C, T0)
  capture\     vault.db (7 days, AES-GCM per record)  index.db (SQLCipher)               (C, T1)
  plugins\<id>\   plugin-data\<id>\
  notes\       quick notes (E11.3; the user's content: kept by `forget all`)
  presets.json window presets (U3 I5; the user's content like notes: kept by both forget commands, included in settings export)
  log\
```
- **Install key** (`state\install.key`):
  - 32 random bytes, protected with DPAPI (current user).
  - B and C's `KeyedHasher(domain)` derive their keys from it with HKDF and a domain label.
  - The T1 vault and index keys are separate HKDF outputs. Tasktor never derives an encryption key from the same output as a hash key.
- **Export / import / reset:**
  - `tasktor settings export <zip>` bundles `config.json`, `rules.json`, the plugin list and consents. It never includes plugin code, `track\`, `capture\` or the install key. Learned state (`job-kinds.json`) is included only with `--include-learned` (B).
  - `import` validates before replacing.
  - `reset` restores defaults and keeps data.
- **`forget` family** (one verb; each asks before deleting, and `--yes` skips the question):
  | Command | Deletes |
  |---|---|
  | `tasktor forget learned` | `job-kinds.json` (B6), the `KindStats` behind A5 / A6 if adopted |
  | `tasktor forget all` | all of the above + `agents\`, `jobs\`, `log\`, `selfuse.json`, `snapshot.json`; settings and rules stay (use `settings reset` for those) |
  The old `delete-all` is folded into `forget all`, and C's `forget --all` maps onto it.

### D-H9 Process topology: the processes and who starts them
| Process | Build | Started by | Lives | Budget (D-H10) |
|---|---|---|---|---|
| `tasktor-core.exe` (agent) | both | logon (MSIX startup task / portable `HKCU\…\Run`, opt-in) | always | idle ≤ 15 MB |
| **tray icon** (inside the agent) | both | agent | always | inside the agent's budget |
| `Tasktor.Flyout.exe` | both | tray click, hotkey, toast, widget deep link | exits ~30 s after hidden (A's OQ9, G decides) | ≤ 30 MB while open **[estimate]** |
| `Tasktor.Widget.exe` (COM provider) | MSIX | Widgets host | while activated | ≤ 60 MB, exits when released |
| `tasktor.exe` (CLI) | both | the user / scripts | one call | — |
| `worker\python\python.exe -I` (learning worker) | both | agent `StartWorker` | one refit, then exits | BelowNormal, ≤ 2 threads, ≤ 1.5 GB |
| content worker (C21 embeddings / OCR) | both | capture `StartWorker` | one batch | BelowNormal |
| `tasktor-nmh.exe` (native messaging host) | both | the browser, per Chrome's native-messaging rules | while the extension connects | tiny |
| plugins | both | plugin host (agent) | one run | ≤ 256 MB, timeout |

**The tray icon lives in the agent ().**
- The portable build has no widget, so the tray is how users reach everything (G6). It must be there whenever the agent runs.
- A `Shell_NotifyIcon` icon with a message-only window and a native popup menu is plain Win32, so it fits a Native AOT agent with no UI framework **[unverified: memory cost, A0]**.
- The flyout, the heavy UI, stays in its own process, as A's D-A3 says.
- G designs the menu; U1 only places the icon.

**Supervision.**
- The agent restarts a crashed capture process at most 3 times per hour, with backoff, and then shows an error card.
- Workers are never restarted automatically; the next refit simply starts a new one.
- The agent itself is restarted by nothing: if it dies, the tray icon disappears, which is honest. The next logon starts it again.
- **Single instance:** a named mutex per user session. A second `tasktor-core` exits after it asks the first to show the flyout.

**`IProcessControl` review (C's A-C4, asked of H): approved, with four conditions.**
1. `StartWorker` takes a `WorkerSpec` whose `kind` is a closed set: `learning`, `content`, `capture`, `plugin`.
   - For every kind except `plugin`, the exe path must resolve **inside the install dir**. Once signed, the signer must match the agent's.
   - So skipping `GuardChain` for Tasktor's own children cannot be turned against another process. `GuardChain` protects other processes from Tasktor's actions; starting a child is not such an action.
2. Every worker gets a kill-on-close Job Object with a memory cap, at BelowNormal (EcoQoS for learning and content). The capture process runs at Normal, because its hook thread must answer within `LowLevelHooksTimeout` (D-C19).
3. `SendControl` messages are a closed enum: `pause`, `resume`, `cancel`, `checkpoint`, `flush`. The same set is used over the pipe (Tasktor's children) and the control file (helper-backed user jobs, B). Anything else is rejected.
4. All three calls run on the off-loop action worker and post their results as events (D-A5 §7).

### D-H10 Budgets for every process, one table
| Process / state | Private memory | CPU | Source |
|---|---|---|---|
| Agent, idle, Core only (tick + B's 60 s scan) | ≤ 15 MB (B's share ≤ 0.5 MB inside it) | ≤ 0.1% | D-A3, D-B8 |
| Agent, idle, T0 tracking on | ≤ 17 MB | ≤ 0.1% | D-C7 (+ ≤ 2 MB) |
| Agent, viewer open | ≤ 30 MB | ≤ 1% | D-A3 |
| Agent, folder scan | ≤ 60 MB | BelowNormal | D-A3 |
| Capture (T1) | ≤ 40 MB | ≤ 0.5% | D-C21 |
| Flyout open | ≤ 80 MB **[estimate, W1; WinUI baseline]** | ≤ 1% idle | new |
| Widget provider, while activated | ≤ 60 MB | ≤ 1% | H draft |
| Learning worker, content worker | ≤ 1.5 GB, ≤ 2 threads | BelowNormal, idle + plugged in only (learning) | H draft, D-C11 |
| Plugin run | ≤ 256 MB | BelowNormal, ≤ 60 s | D-H7 |
| Disk writes while idle, T0 off | < 1 MB / hour | — | H draft |
| Start to first snapshot | < 1 s | — | H draft |

How it is enforced:
- `SelfMonitor` checks the agent against its row. Its built-in rule logs a breach, and a second breach notifies (D-A3).
- The agent watches the capture process's row the same way.
- The CI `perf` job asserts the idle rows on a 4-core Windows runner and keeps the numbers as build artifacts. The reference PC (i7-1355U / 12 GB) is measured before V1.

### D-H11 Quality gates, enforced in CI
| Gate | How | Runner |
|---|---|---|
| G-schema: contracts round-trip | C# → JSON → Python validate → C#, on fixture files for every public contract | Linux |
| G-perf (H5) | D-H10 idle rows | Windows |
| G-aot: Native AOT publish has no trim / AOT warnings | `dotnet publish -p:PublishAot=true` with warnings as errors | Windows |

### D-H12 T1 ships in the public release; signing is attempted, never a blocker
**the maintainer's decision:** deep capture is part of the public release. Try to get `tasktor-capture.exe` signed. If that cannot reasonably be done, ship it unsigned and let users handle the antivirus alerts.

**Signing routes, tried in this order:**
| Route | Cost | Fit | Risks |
|---|---|---|---|
| 1. **SignPath Foundation** (free code signing for OSS) | free **[verified: signpath.org]** | Apache-2.0 (OSI, no dual licence), a public repo, binaries "built from source code in a verifiable way" by automated CI, MFA for every team member, manual approval of each signing request, a code-signing-policy page with attribution and a privacy statement **[verified: signpath.org/terms]** | they may reject at their discretion. The software must not be "potentially unwanted" or "compromise the privacy or security of users", and a keystroke recorder is exactly what a reviewer looks at hard. The publisher shown is SignPath Foundation, not the maintainer. The project "must already be released" in the form to be signed, so the first release is unsigned anyway. The role structure (author / reviewer / approver) assumes a team; whether a solo maintainer qualifies is **[unverified: ask them]** |
| 2. **Azure Artifact Signing** | paid, ~$10 / month **[verified 2026-11-03, Frame; current price not shown on the pricing page 2026-11-04]** | individuals in the US or Canada are eligible for Public Trust certificates **[verified: learn.microsoft.com artifact-signing quickstart]**; a GitHub Actions integration exists | onboarding of individuals was limited further during the preview **[secondary: Microsoft Q&A, 2026-03]**. Needs the user's OK to pay |
| 3. **Microsoft Store** (MSIX) | free for individuals | the Store signs the package | whether certification accepts a global LL keyboard hook in a desktop app is **[unverified: no policy text found 2026-11-04]**. It covers only the MSIX build, not the portable zip |
| 4. **Unsigned, with guidance** | free | always available | SmartScreen "Run anyway"; antivirus may flag the hook binary |

**What makes the capture binary look as little like malware as possible, signed or not.** Most of this is already decided in C and U1:
- **One small binary holds the hook** (`tasktor-capture.exe`). The agent, CLI and viewers carry no hooks, so a flag on capture never takes the rest of Tasktor down with it.
- **No network code** (G-capture-net, D-H11). Nothing it records can leave the PC by itself, and the README says so in one line.
- **Opt-in and visible:** off by default, its own consent screen, an always-visible indicator, current user only, never hidden (D-C22).
- **Verifiable builds:** CI builds every release from a tag. Releases publish `SHA256SUMS.txt`, and CONTRIBUTING documents how to build the same binary from source.
- **Before each release, submit the capture binary to Microsoft's false-positive portal** (Defender's file submission for developers) **[unverified: the exact portal name and turnaround, V1]**.
- **README "Antivirus warnings" section:**
  - why the warning appears;
  - how to check the hash;
  - how to allow the one file in Defender;
  - that T0 tracking and the rest of Tasktor work without it.

**Decision: go unsigned.** V1 and later releases ship unsigned, with T1 included, using route 4 and the guidance above. Routes 1–3 stay recorded as options he can take up later; none is planned.

### D-H13 License: Apache-2.0
Both Apache-2.0 and MIT are permissive. Apache-2.0 adds an explicit patent grant and patent retaliation. The old reason, vendoring agent-desktop, is gone: agent-desktop is a reference only (D-C14). The patent grant still matters for a tool that ships learning code and hooks. MIT stays the alternative if he prefers the shorter text.

## H-by-H (what each row now says)
- **H1 First run.**
  - The first launch starts the agent with the tray icon and the Core cards (A, B) only. No wizard, no account, no prompt.
  - Each Module asks when it is turned on, in one dialog that says what it reads and where it is stored. The modules are T0 tracking, T1 capture (with its own consent screen, D-C22), plugins, and U2's sources.
  - "What Tasktor can see" in the tray menu becomes C's **"What Tasktor knows"** view (C3.6); G designs it.
- **H2.** Privacy rule 1 as a test: G-net (D-H11). The update check (H13) is the one Core-adjacent connection and is off until allowed.
- **H3.** D-H8 (layout, export, `forget`) + D-H5 (format, migration).
- **H4.** Every viewer follows the Windows theme, accent, high contrast and "Animation effects". No viewer has its own theme engine. The look is G's.
- **H5.** D-H10.
- **H6 Logging.**
  - A small file logger in the agent, using source-generated `LoggerMessage` (AOT-safe), not Rust `tracing`.
  - Daily files, keep 7, 20 MB cap.
  - `info` by default; `tasktor log verbose on` turns verbose on, and it reverts by itself after 24 h.
  - **No personal data at any level.** Window titles, command lines, paths under the profile, URLs, plugin output and typed text are `Sensitive<T>` and print `<redacted>`. G-redact enforces it.
  - `tasktor log bundle` zips the logs + config (no data dirs) and shows the file list first.
- **H7 Tests + CI.**
  - Tests: xUnit for Contracts / Core (Linux) and Windows / Hosts (Windows runner), `pytest` for the worker, schema fixtures shared by both languages.
  - GitHub-hosted runners are free for public repos **[verified: H draft, docs.github.com]**.
  - **A Linux cloud session can verify:** contracts, schemas, rules evaluation, the action router, plugin host logic against a fake runner, the episode builder and clustering, card JSON, docs.
  - **It cannot verify:** PDH, WinEvent hooks, UIA, toasts, Job Objects, the pipe ACL, MSIX, the widget host.
  - The Windows runner does the AOT publish. Whether its image has the C++ desktop workload is **[unverified, A0]**.
  - The widget board and sideloading stay manual, in `docs/manual-checks.md`.
- **H8 Builds.**
  - **Portable zip:** the same, without the widget. Logon start is through an opt-in `HKCU\…\Run` entry.
  - **Python:** the embeddable 3.13 package + vendored wheels via `pip install --target` (D-C11), run with `-I`. The embeddable zip is meant for embedding in a native app and is isolated from the host's environment **[verified: docs.python.org using/windows]**. Size is dominated by numpy / scipy / scikit-learn, est. 80–120 MB unpacked **[unverified, L1]**.
- **H9 Docs.**
  - README: the privacy promise verbatim, both builds, what SmartScreen says, T0 vs T1 explained.
  - LICENSE (D-H13), NOTICE, CONTRIBUTING (the "no machine-specific paths" rule).
  - `docs/plugins.md` (D-H7, 20-line example), `docs/rules.md` (D-H6), `docs/contracts.md` (D-H1 table), `docs/manual-checks.md`.
  - Issue templates.
- **H10.** D-H6.
- **H11.** D-H7.
- **H12 Distribution.**
  - GitHub Releases, built by CI from a `v*` tag: the MSIX, the portable zip and `SHA256SUMS.txt`.
  - The portable zip is the public download until signing.
  - The Store and winget come later.
- **H13 Update check.**
  - Portable: off by default. When allowed, it checks the GitHub releases API at most once a day and **notifies only**.
  - Sideloaded MSIX: an `.appinstaller` file **[verified: H draft]**.
  - The Store updates its own installs.
- **H14 Self-use counts.**
  - `state\selfuse.json`: counts per feature (cards opened, actions used, rules fired). Numbers only, never sent anywhere.
  - Shown in Settings → About; cleared by `forget all`.
- **D1.** D-H2 + D-H3 + D-H4: `tasktor status [--json] [--cards]` prints `state` (and `cards` when asked), with exit 3 when the agent is not running.
- **D5 `tasktor summary [--since 7d] [--json]`.**
  - Prints time per workflow (with the user's names), switches per day, and top apps by time, from C's episodes.
  - It never includes titles, typed text or raw events. It works only when T0 tracking is on.
  - It obeys C's rule 5b: words appear only if they recur.
  - This is the "bounded summary for an agent" from the Frame.
- **I1 agent hook file (format only; U2 designs the feature).**
  - `tasktor agent-hook <state> [--agent <name>]` reads the agent's hook JSON on stdin. It keeps **only** `session_id`, `cwd` (last segment) and the event name, and writes `agents\<session>.json` as `{schema:"tasktor.agent/1", agent, session_id, folder, state, since, pid}`.
  - States are `working`, `needs-permission`, `waiting`, `ended`.
  - Prompts, transcripts and messages are never read or stored.

## Architecture (builds on D-A5; no new interface)
**Where U1 sits** (data flows left to right, actions flow back):
```
 sources ─enqueue─► logic loop (Core) ──► SnapshotPublisher ──► snapshot.json ─────────► CLI · plugins · scripts
                       │  ▲                   └──► PipeServer ──delta / pull──────────► flyout · widget · tray
                       │  └──────── ActionRouter ◄──act── viewers · CLI · toast · rules · plugin buttons
                       ▼                  │ origin policy → GuardChain → handler (off-loop)
                 RuleEngine ──requests──►─┘
                 PluginHost ─StartWorker─► plugin runs (Job Object) ─stdout cards─► ICardProvider
```
**New classes (Core unless noted).** All of them compose A's types. Interfaces stay at seven, plus G's `IBlockRenderer`.
| Class | Responsibility | Composes | Cost |
|---|---|---|---|
| `ActionRouter` | D-H4 steps 1–6; issues and checks confirm tokens | `ActionCatalog`, `OriginPolicy`, `GuardChain`, `IProcessControl`, feature handlers | O(guards) per action |
| `OriginPolicy` | the origin × class table; reads `cli.allow_destructive` | settings | O(1) |
| `StateTracker` | holds the `state` tree (the Contracts state records); dirty-tracks fields; produces JSON Patch deltas | A's tables, B's `JobRow`, C's `CurrentWorkflow` | O(changed fields) |
| `CardTemplates` | card structures from every `ICardProvider` with bindings; republished only on structure change | `ICardProvider` | O(cards) on change |
| `SnapshotPublisher` (A, extended) | writes `snapshot.json` ≤ 1/s on change; feeds `PipeServer` | `StateTracker`, `CardTemplates` | O(size) per write |
| `PipeServer` (Windows) | pipe DACL, `hello`, `subscribe` / `pull` / `act` / `lease`; one task per client; posts requests to the loop | `EventChannel`, `Scheduler` leases | O(clients) |
| `RuleSet` + `RulesLoader` | parse + schema-check `rules.json`, hot reload, error card; overlays shipped defaults by id | A's `RuleEngine` (unchanged), D-H6 condition / action classes | O(rules) on change |
| `PluginHost` | discovers `plugins\`, consent + hash pin, schedules runs through `StartWorker(kind=plugin)`, converts output to cards | `IProcessControl`, `ICardProvider`, `ConsentStore` | O(plugins) |
| `StoreLayout` | every path from Known Folders + the portable override; nothing else builds a path | `IClock`-free, pure | O(1) |
| `SettingsStore` | `config.json` load / migrate / `.bak` / save (byte copy to a worker, D-A5 §7) | `StoreLayout` | O(size) |
| `ConsentStore` | consents (modules, T1, plugins + pinned hashes) | `StoreLayout` | O(1) |
| `InstallKey` | creates / unprotects the DPAPI key; HKDF derivation per domain for `KeyedHasher` and the T1 vault | Windows DPAPI behind `Tasktor.Windows` | O(1) |
| `Forgetter` | the `forget` family; asks the owning features to stop and close their stores first | features' `Disable()` | O(files) |
| `TrayIcon` (Windows, in the agent) | `Shell_NotifyIcon`, menu built from the action catalog, launches the flyout | `ActionRouter` | event-driven |
| `LogSink` + `Sensitive<T>` | file logger, redaction, verbose auto-revert | `StoreLayout`, `IClock` | O(1) per line |
| `SelfUseCounter` | H14 per-feature counts in `state\selfuse.json`; numbers only, cleared by `forget all` | `StoreLayout`, `SettingsStore` persistence path | O(1) per count |

**Time and space (U1's share).** `n` = changed state fields, `c` = cards.
| Operation | Naive | Design |
|---|---|---|
| Push state to viewers | resend the whole snapshot each sample | JSON Patch of the n dirty fields, coalesced to ≤ 1 per 500 ms per client |
| Write `snapshot.json` | every sample | on change, ≤ 1/s, atomic replace; ~64 KB **[estimate]** |
| Cards | rebuilt every sample | rebuilt on structure change only (bindings carry the values) |
| Rules reload | restart the agent | parse + validate on file change; swap the `RuleSet` on the loop |
| Action | per-surface code paths | one router; O(guards) |
| Plugin run | resident plugin processes | not resident; one process per run in a Job Object |

In order of dependency:
1. `StoreLayout`
2. `ActionCatalog` + `OriginPolicy`
3. `ActionRouter`
4. `StateTracker`
5. `RulesLoader`
6. `PluginHost`
7. `Forgetter`


## Diagrams
Three diagrams were drawn in the planning chat on 2026-11-04:
- `u1_process_topology_contracts`: clients → agent → children;
- `u1_action_path_router`: five origins → one router, six steps;
- `u1_state_cards_binding`.

They are drawn from this file, and this file wins if they differ.

## Open questions (probes, with working defaults)
| # | Question | Default | Checked |
|---|---|---|---|
| OQ-H1 | Store acceptance of `unvirtualizedResources` | use it; fall back to pipe-only readers for MSIX | V1 |
| OQ-H2 | Tray icon memory inside a Native AOT agent | ≤ 1 MB | A0 |
| OQ-H3 | Windows runner image has the C++ desktop workload for AOT publish | yes, else install it in the workflow | A0 |
| OQ-H4 | Defender's reaction to an unsigned LL-hook binary; SignPath's answer for a solo maintainer + an input recorder | ship anyway with the README guidance (D-H12) | V1 |
| OQ-H5 | Flyout (WinUI) private memory while open | ≤ 80 MB | W1 |
| OQ-H6 | Widget host update rate honoured (G's coalescing) | ≤ 1 update / 2 s | W0 (G) |

## Changes for the catalog (for the parent session to merge at signing)
- FEATURES H rows:
  - H3 adds the `forget` family.
  - H5 becomes the D-H10 table.
  - H10: format is JSON (not TOML), toasts allow 3 buttons.
  - H11: blocks output.
  - Add H15 process topology.
- FEATURES D1 / D5 point to U1.
- `00-frame.md`:
  - C1.7 root → `%LOCALAPPDATA%\Tasktor` and the D-H8 layout.
  - C1.8 → the D-H3 pipe + D-H4 action verb.
  - C4 → D-H7 (blocks, consent with hash pin).
  - C5 → D-H9 processes, D-H12 unsigned releases with T1 included.
  - The license line → Apache-2.0.
- A: `IProcessControl` gets "H review: approved with D-H9's four conditions". `SnapshotPublisher` gets "extended by U1 into `StateTracker` + `CardTemplates`".
- B: the toast cap conflict is resolved (3 buttons); export uses `--include-learned` (unchanged).
- C: `forget --all` → `tasktor forget all` / `forget capture` / `forget tracking`; the store root is settled; the capture budget is recorded, and T1 ships in V1 (D-H12).
- G (U4) receives:
  - the bindings in cards, which renderers resolve;
  - the tray icon living in the agent (G designs its menu);
  - the plugin block subset.

## Consolidated at signing (2026-11-04)
U1 is signed. This file does **not** edit other sections. Each row below is a change for its owner. "Must" means another section depends on it.

**U1 decisions in one place**
| Id | Decision |
|---|---|
| D-H1 | Every public format is a C# record in Contracts; CI generates JSON Schemas; public contracts have a stability promise, internal ones do not |
| D-H2 | Snapshot = `state` (data) + `cards` (A's blocks bound to state by JSON Pointer); plugins send literal values |
| D-H3 | `snapshot.json` (atomic, ≤ 1/s on change) + a current-user named pipe (`hello` / `subscribe` / `pull` / `act` / `lease`); no TCP, ever |
| D-H4 | One `ActionRouter` (catalog → origin policy → `GuardChain` → confirm token → off-loop handler → log); CLI destructive actions need `--yes` + `cli.allow_destructive` (off) |
| D-H5 | `config.json` / `rules.json` are JSON with comments and `$schema`, not TOML |
| D-H6 | Rules grammar v1: reversible actions only, `undo`, toasts ≤ 3 buttons, `run` only from a clicked toast button |
| D-H7 | Plugins emit a fixed subset of A's blocks; one process per run, 256 MB, timeout; consent pinned to hashes |
| D-H8 | Root `%LOCALAPPDATA%\Tasktor` (MSIX excluded-directory fix); one install key (DPAPI + HKDF); `forget learned / tracking / capture / all` |
| D-H9 | Process topology; tray icon in the agent; capture restarts ≤ 3 / hour; `IProcessControl`'s 3 new members approved under 4 conditions |
| D-H10 | One budget table for every process |
| D-H11 | CI gates: G-net, G-capture-net, G-schema, G-redact, G-perf, G-aot |
| D-H12 | Unsigned releases, T1 included; README antivirus guidance; signing routes kept for later |
| D-H13 | Apache-2.0 |

**A (system monitor): changes needed**
| # | Change | Kind |
|---|---|---|
| A-H1 | `IProcessControl`: StartWorker / CancelWorker / SendControl → "H review: approved with D-H9's four conditions" (closed `WorkerSpec.kind`, install-dir paths, Job Object per worker, closed `ControlMessage` set, off-loop) | must |
| A-H2 | `SnapshotPublisher`: extended by U1 into `StateTracker` + `CardTemplates`; writes ≤ 1/s on change | must |
| A-H3 | D-A5 §4 Contracts list gains the U1 records (snapshot, action, rules, plugin, agent hook, `WorkerSpec`, `ControlMessage`) | must |
| A-H4 | D-A3 viewer row: the tray icon lives in the agent (no separate tray process) | must |
| A-H5 | `rules.json` / `config.json` stay JSON (D-H5), so A's wording is unchanged | — |

**B (long-job watch): changes needed**
| # | Change | Kind |
|---|---|---|
| B-H1 | Toast cap conflict resolved: 3 buttons (D-H6), so B2.4's silent toast stands | — (resolved) |
| B-H2 | The CLI's job verbs go through the `ActionRouter` (`tasktor job lower / restore / pause / resume / end`); `end` needs `--yes` + `cli.allow_destructive` | must |
| B-H3 | The progress file schema `tasktor.job/1` is hosted in `schemas/` (D-H1); B still owns it | must |
| B-H4 | `forget learned` replaces B6's "Forget" wording for the CLI; the B6 UI button calls the same action | must |
| B-H5 | `ControlMessage` (pause / resume / cancel / checkpoint / flush) is the closed set for B's polite pause control file | must |

**C (workflow learning): changes needed**
| # | Change | Kind |
|---|---|---|
| C-H1 | Store root settled: `%LOCALAPPDATA%\Tasktor	rack\` and `capture\` (D-H8) | must |
| C-H2 | `forget --all` → `tasktor forget all`; also `forget tracking`, `forget capture` | must |
| C-H3 | Vault and index keys come from `InstallKey` HKDF outputs separate from `KeyedHasher`'s | must |
| C-H4 | T1 ships in the public release unsigned (D-H12); README antivirus section; the capture binary keeps no network code (G-capture-net) | must |
| C-H5 | `tasktor summary` (D5) reads C's episodes under rule 5b | must |
| C-H6 | `WorkflowState` in the snapshot = C's `CurrentWorkflow` (id, name, confidence, source, level) | must |

**U2 (sources, guards + watches):**
- New conditions and states (`in_call`, `file.created`, connectivity, calendar) go into the D-H6 grammar as catalog rows.
- I1 behaviour builds on `AgentHookFile` (`tasktor.agent/1`).
- I12 uses `run` from a toast button.

**U3 (windows):** F's window events feed rules' `event` and `state: fullscreen`.

**U4 (surfaces):**
- Renderers resolve bindings.
- The tray menu is built from `ActionCatalog`.
- The flyout is ≤ 80 MB (OQ-H5).
- Plugin cards use the block subset with a `source` badge.
- Destructive buttons follow D-H4's two-step rule.


**Frame (`00-frame.md`):**
- C1.7 → D-H8.
- C1.8 → D-H3 + D-H4.
- C4 → D-H7.
- C5 → D-H9 + D-H12.
- License → Apache-2.0.
