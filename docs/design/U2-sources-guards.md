# Tasktor: U2. Sources, guards and watches (feature spec)

**Status:** **signed**. D-S1–D-S7 decided; E11 reshaped into a focus Module; I1 Q1 / Q2 and OQ-S6 decided. Open items are probes only (OQ-S1–OQ-S5).

- Music (E4–E6) and weather (E7) are cut.
- E3 and I8–I10 go to U5 (the pack).
- I5 goes to U3.

It replaces the drafts `I-tailored.md` (I1 carried forward) and `E-glance-modules.md` (verified facts carried forward).

**Fixed inputs (signed):**
- **A:** the four shapes and seven interfaces (D-A5); `Scheduler` / `DemandLease`; `TreeRollup`; `RuleEngine` + `LearnedThreshold`; `PowerEvents`; the A5 disk flyout; D-A3 budgets.
- **B:** `JobGate`'s windowed-busy classifier; pause / freeze / restrain through `IProcessControl`; B3.5's core cap moves to I4; `KindStats`.
- **C:** `WinEventSource` / `IdleSource`; C9 time per workflow; T1 actor attribution reads the agent list; refits run only when idle and plugged in.
- **U1:**
  - rules grammar v1: reversible actions only, `undo`, `run` only from a clicked toast (D-H6);
  - the action router (D-H4); the plugin contract (D-H7);
  - the `tasktor.agent/1` hook file format;
  - the store layout (D-H8);
  - G-net: "Core makes zero outbound connections" (D-H11).

**Reference low end:** i7-1355U, 12 GB RAM, one monitor.

Conventions:
- **[verified: source]** = checked this session or carried from a verified draft.
- **[unverified]** = still a claim, checked in the named phase.
- **[community]** = a non-Microsoft source.
- **[estimate]** = a reasoned number, to be measured.

## Why U2 exists (the failure it prevents)
Fifteen catalog rows each looked like their own feature. Underneath they are **a handful of new data sources plus rules over them**, and the rules end in actions that A and B already built:
- E1, E2, I3 and I13 all read a calendar.
- I2, I3, I4 and I13 all end in B's pause / restrain.

Designed one row at a time, they would get four calendar readers and four pieces of guard plumbing. U2 designs **each source once** and writes the rest as **default rules** in U1's grammar.

## E11 Focus timer + quick notes
**Requirement:** keep a Pomodoro and some notes tools; let users **define their own timer fits**, not only 25/5; and **cross-validate the results with workflow tracking**.

**Cut:** clipboard history. Windows has Win+V, and clipboard contents are personal data Tasktor keeps out of everything.

**E11.1 Timer presets (Module, off by default).**
- A preset is a named sequence of blocks: `{name, blocks: [{kind: focus|break, minutes}], repeat, long_break_every?}`.
- Shipped presets: Pomodoro 25/5 (long break 15 every 4), 50/10, 90/20.
- The user adds their own in Settings or in `config.json` (`focus.presets[]`, schema-checked like everything else).
- At start, the user may pick an **intended workflow** from C's named workflows, or leave it as "whatever I'm doing now", which takes C's current workflow at start.
- While a focus block runs:
  - it is a short `event_mode` window (I13), so refits and the content worker pause (D-S4 default rules);
  - the card shows a `Countdown` block with Start, Pause, Skip and Stop.
- Block ends raise a toast with Next / +5 min / Stop. This is a rule (`focus-block-end`), on while the Module is on.

**E11.2 Cross-validation with workflow tracking (needs T0 tracking on; without it, the timer works and this part says "turn on tracking to see how focused blocks went").**
After each focus block, `FocusReview` reads C's activity data for that block and stores **numbers only** (rule 5b):
| Measure | From | Meaning |
|---|---|---|
| on-intent share | C's workflow per minute (C9 human time) | share of the block spent in the intended workflow (or the start workflow) |
| switches | C10 | workflow switches inside the block |
| idle share | `IdleSource` | share of the block with no input |
| agent share (T1 only) | C16 actor attribution | share driven by an agent, not counted as on-intent human time |
| completed | timer | finished, skipped or stopped early |

- **Per preset**, `KindStats`-style rolling statistics (last 20 blocks) give mean on-intent share, switches per block and completion rate.
- The card compares presets **only after ≥ 5 blocks of each** (C's trust-ramp idea, D-B9 / D-C5). An example line: "50/10 kept you on course longer than 25/5 (82% vs 71%, 12 blocks each)".
- **Report only.** It suggests, and never switches the user's preset by itself.
- Guardrail: if C's confidence for a block is below its floor, that block is excluded and the card says how many were excluded.
- Storage: `stateocus.json` (preset stats + the last 200 block results, numbers and workflow ids only). It is cleared by `tasktor forget tracking` and `forget all`, because it derives from tracking.

**E11.3 Quick notes.**
- A few plain-text notes under `notes\` in the store, edited in the flyout. Cards show the first lines of pinned notes.
- **T1 capture never records typing into Tasktor's own windows** (C's suppression list gains Tasktor's processes).
- Notes are the user's own content, like settings: `forget all` keeps them, `settings export --include-notes` includes them, and they are never sent anywhere.

## Features (U2)
| # | Feature | Tier | Built from | Phase |
|---|---|---|---|---|
| I1 | AI agent watch: running agents, busy / idle (estimated) + working / needs-permission / waiting (reported by hooks) | Module | `agents` group + `JobGate` busy + `AgentHookSource` | T2 |
| I2 | Overlap guard: more than N busy processes of a group | Core rule, off | default rule over `group` + busy | W1 |
| I3 | Meeting guard: in a call → pause refits, lower / pause opted-in job groups | Module | `CallStateSource` + `CalendarSource` + default rule | T2 |
| I4 | Power guard: on battery → pause refits (on); restrain or core-cap opted-in groups (off) | Core rule | `PowerEvents` + default rules; B3.5 core cap lands here | W1 |
| I6 | Time per project: map workflows to projects; weekly totals | Module (needs T0) | C9 + `ProjectMap` | L2 |
| I7 | Space report: named caches, Downloads, %TEMP%, OneDrive kept on device, user folders (report only) | Core | `FolderTreeSource` + `TreeRollup` + `CacheLocator` (tab in A5's disk flyout) | W1 |
| I11 | Required-window watch: warn when a chosen window disappears | Module | `WinEventSource` (lease) + default rule | T2 |
| I12 | Folder watch with an action: new file → toast with a declared command button | Module | `FolderWatchSource` + rule `run` (U1) | T2 |
| I13 | Event mode: a named time window → countdown, refits paused, optional layout (U3) | Module | `EventWindow` + `CalendarSource` + default rules | T2 |
| I14 | Connectivity watch: drops, captive portal, Wi-Fi changes; top connection-holding processes | Module | `ConnectivitySource` + `ListenerTableSource`'s sibling | T2 |
| E1 | Deadlines countdown from a plain file or any ICS calendar | Module | `CalendarSource` (+ `tasktor.calendar` fetch plugin for URLs) | T2 |
| E2 | Next event: countdown, rest-of-day, urgency, "Join" toast for meeting links | Module | `CalendarSource` + default rule | T2 |
| E8 | Bluetooth device batteries | Module | `BluetoothBatterySource` | later (limited) |
| E10 | Repo hygiene: dirty, unpushed or behind repos under chosen folders | Module | first-party plugin `tasktor.repos` | later |
| E11 | Focus timer with custom presets, cross-validated against workflow tracking; quick notes; **no clipboard history** | Module | `FocusTimer` + `FocusReview` (C9 / C10 / `IdleSource` / C16) + `EventWindow` + notes store | T2 (timer, notes) / L2 (validation) |

## Cross-cutting decisions

### D-S1 Every U2 feature = a source + default rules + blocks; no new interface
Each row reduces to three things:
1. **Sources.** A source implements A's `IMetricSource`, `ITableSource` or `IEventSource`, holds a `DemandLease`, and only enqueues (D-A5 §7).
2. **Default rules.** They ship read-only in the install dir and are written in U1's grammar. Each row's rules are listed in its section, with on / off defaults. The user can override any of them by `id`.
3. **A feature class.** It derives from `Feature`, implements `ICardProvider`, and builds its card from A's blocks.

New **condition and state names** are rows in the rule catalog, not grammar changes:
- states: `in_call`, `meeting_now`, `event_mode`, `on_battery` (exists), `offline`, `captive_portal`;
- events: `file.created`, `window.missing`, `agent.state`, `net.dropped`.

**Why:** the slot map in D-A6 already promised this. The only new type kinds are source classes.

### D-S2 The agent never opens a network connection; network Modules are first-party plugins
**The problem.** E1 / E2 fetch ICS URLs and E10 may fetch git remotes. U1's G-net test says Core makes zero outbound connections. Putting a fetcher in the agent would turn G-net from "never" into "never, except when…".

**The decision.**
- Every U2 feature that touches the network ships as a **first-party plugin**: `tasktor.calendar` (ICS URLs) and `tasktor.repos` (`git fetch`, only if the user turns on "check remotes").
- They live in the install dir under `plugins-builtin\` and are off by default.
- They use U1's contract **unchanged**: consent dialog, hash pin, one process per run, Job Object, timeout.
- Plugins already may write their own `plugin-data\<id>\`.
  - The calendar plugin writes a normalised `events.json` there (`tasktor.calendar/1`, a small new public contract).
  - The agent's `CalendarSource` reads that file plus any **local** `.ics` file, with no network.

**Result:**
- G-net tightens to "**the agent process never connects**". It is tested on the agent with every Module on, not only Core.
- The README promise becomes one plain sentence: "only plugins you enable can use the network, and each one says where".

**Follow-up for U1 (can, not must):** H13's update check could move into a first-party plugin the same way, so even it leaves the agent.

### D-S3 One agent list, owned by the rules file
- The list of AI agents is the **`agents` group in `rules.json`**: process names plus AUMIDs (U1's `groups` block already exists).
- Shipped defaults:
  - `claude.exe`, `codex.exe`, `cursor.exe`, `aider` (Python entry point), `gemini` (Node entry point), `opencode.exe`, `agent-desktop.exe`;
  - the Codex computer-use helper;
  - `AutoHotkey*.exe` (a known input injector, for C16).
- One list, read by three users, owned by none:
  - B's watch-list includes `group:agents`;
  - C's `ActorAttributor` reads it (B-C3 satisfied);
  - I1's card reads it.
- The user edits it in one place, and A's grouping (A2) can also show the group.
- **Script-hosted agents** (`node …/claude`, `python -m aider`) are matched like B's job kinds: exe + script basename, using B's matcher in `JobGate`. They are never matched on the full command line.

### D-S4 Guards are default rules over B's actions, all reversible
I2, I3, I4 and I13 add **no action code**. Each is a few default rules whose `then` uses only U1's reversible actions, with `undo: true`:
- `pause refits`;
- `set_priority`;
- `efficiency`;
- `pause job:<group> mode polite|freeze`;
- the B3.5 **core cap**, added to the grammar as a reversible action `cap_cores` (Job Object CPU rate or affinity, released by `undo` or by the "Release all caps" action).

**Opt-in is per group.** A guard acts only on groups the user ticked in its settings (default: none), except "pause refits", which is Tasktor's own work. So no guard ever slows down an app the user did not name.

### D-S5 Privacy of the new sources
| Source | Reads | Never reads or stores |
|---|---|---|
| `CallStateSource` | which app has the **microphone** or camera in use (on / off) | audio, video, who is in the call |
| `CalendarSource` | event start, end, title, location / meeting URL for the next 14 days, **in memory only** | descriptions, attendees; titles are never written to the log (`Sensitive<T>`) or a store |
| `AgentHookSource` | `agents\*.json` as U1 defines it (state, folder name, pid) | prompts, transcripts, messages |
| `FolderWatchSource` | new file names in folders the user chose | file contents |
| `WinEventSource` (I11) | whether a window matching the **user's own** pattern exists | other windows' titles (matched in memory, not kept) |
| `ConnectivitySource` | connected / offline / captive portal, Wi-Fi signal; "network changed" | SSIDs, BSSIDs, location |
| `BluetoothBatterySource` | paired device name + battery % | anything else on the device |
| `tasktor.repos` | `git status` summary counts | file contents, diffs, commit messages |
| `CacheLocator` / I7 | folder sizes | file names beyond the top-N list shown on demand |

A secret ICS URL is a credential. It is stored in the calendar plugin's settings, protected with DPAPI (current user), and it is never in `config.json`, the log or an export (U1's "credentials stay in the plugin").

### D-S6 Budget: all U2 sources are event-driven or lease-driven
- **No U2 source polls while nothing reads it.** Most are OS notifications:
  - registry change notification (mic);
  - `ReadDirectoryChangesW` (folders, hook files);
  - NLM events (connectivity);
  - WinEvents (windows);
  - `PowerEvents`.
- Countdowns are computed when a viewer asks, not ticked.
- `BluetoothBatterySource` polls every 10 min, and only while its card is pinned or visible (a lease).
- **Agent cost with every U2 Module on:** ≤ +1 MB private, no extra wake-ups when idle **[estimate, T2]**. That keeps the D-H10 idle row at ≤ 15 MB (Core) / ≤ 17 MB (T0) plus this margin. U2 asks H to record it as "+ ≤ 1 MB with U2 Modules".
- Plugin runs (calendar, repos) are outside the agent budget (D-H10's plugin row).

### D-S7 Export my data, for any analysis
**Requirement:** an option for users to export their own data for whatever analytics they want.

**One command, one Settings button.** `tasktor export data [--to <folder>] [--since 30d] [--include-raw] [--include-capture] [--format csv|jsonl|both]`, and in the flyout: Settings → Your data → Export.
- Output: a folder (or `.zip`) with a `manifest.json` (`tasktor.export/1`: export time, Tasktor version, the schema id of each table, row counts, what was left out) and **one CSV + one JSONL file per table**.
- Schemas are generated from the C# records like every public contract (D-H1), so the columns are documented.
- **Python helper:** `tasktor_data.load(folder)` returns a dict of pandas DataFrames, so a notebook can start in one line. It ships in the repo and is installable with pip. It reads exports only, so it never locks the live stores. C14's read-only store API stays for live use.

**What is in an export:**
| Table | From | Default |
|---|---|---|
| `jobs_runs`, `job_kinds` | B (last 20 runs per kind, learned stats; kind ids are hashed, so no names) | in |
| `activities`, `episodes`, `workflows`, `workflow_time_daily`, `switches_daily` | C T0 (rule 5b: ids, user-given names, numbers) | in, when tracking was on |
| `projects`, `project_time_weekly` | I6 | in |
| `focus_blocks`, `focus_presets` | E11 | in |
| `connectivity_hourly` | I14 drop counts | in |
| `system_last_hour` | A's hour ring (memory only; exported as it is now) | in |
| `alerts_log`, `actions_log` | rules fired and actions taken (from H6; names only, no command lines) | in |
| `selfuse` | H14 | in |
| `events_raw` | C T0 raw events (≤ 30 days; **includes window titles**) | only with `--include-raw` |
| `capture_*` (key runs, screen-text diffs, media, CLI log) | C T1 vault (≤ 7 days), decrypted | only with `--include-capture` |

**Never in an export:**
- the install key, consents and plugin secrets;
- other plugins' data (a plugin may offer its own export);
- the index files themselves (`index.db` is rebuilt from the vault; it is not raw data).

**Safety:**
- Exports are **plain text on disk**. The dialog and the CLI say so before writing.
- `--include-raw` and `--include-capture` are two-step in the flyout and need `--yes` on the CLI, because they write titles and typed text in the clear (D-H4: sensitive, treated like destructive for confirmation).
- The default destination is `Documents\Tasktor exports\`. When the destination is inside a cloud-synced folder (OneDrive, Dropbox, Google Drive roots, detected by Known Folder and path), the confirm step warns.
- Exporting never uses the network. The action is logged with counts, never content.

**Catalog:** new row **H16 Export my data** (Core). C14 stays as the tracking-specific part, now served by this export.

## Sources (designed once)

### `CalendarSource : ITableSource<EventKey, CalEvent>` (E1, E2, I3, I13)
**Inputs:**
- (a) local `.ics` files the user picks;
- (b) `plugin-data\tasktor.calendar\events.json` from the fetch plugin;
- (c) a plain deadlines file `deadlines.txt`, one per line: `2026-11-20 17:00  Project report`.

**Reading.**
- It parses `VEVENT` with `DTSTART` / `DTEND` / `DURATION`, `RRULE` (FREQ DAILY / WEEKLY / MONTHLY / YEARLY, INTERVAL, COUNT, UNTIL, BYDAY), `EXDATE`, `RECURRENCE-ID` overrides, and `VTIMEZONE` via Windows time zones.
- **Window: the next 14 days, ≤ 200 instances kept.** It re-expands on file change and at midnight.
- **Parser:** Ical.Net (MIT) if a Native AOT spike shows no trimming warnings. Otherwise, a small in-repo reader for the subset above, which covers what Google, Outlook and iCloud export **[unverified: AOT, T2 spike]**. Whichever is used, the fetch plugin and the agent share one normalised format (`tasktor.calendar/1`), so the plugin may use Python's `icalendar` and the agent only reads JSON.

**Outputs:**
- the next event and the rest of the day;
- `meeting_now`: an event in progress with a meeting URL in its location or URL field;
- deadlines (E1);
- `event_mode` windows (I13).

**Meeting URL detection:** host matches against `teams.microsoft.com`, `zoom.us`, `meet.google.com` and `*.webex.com`, applied to the location / URL field only.

### `tasktor.calendar` (first-party plugin, D-S2)
- Fetches ICS URLs on its interval (15 min default) with `If-None-Match` / `If-Modified-Since`.
- Expands the same 14-day window and writes `events.json`.
- Its card shows "calendars: 2 ok, 1 error".
- Outlook / Google sign-in (an OAuth device flow) is a **later** plugin version. U2 ships ICS only.

### `CallStateSource : IEventSource<CallState>` (I3)
- Windows records microphone use per app under `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\` (`NonPackaged\<path>` and per-package keys). `LastUsedTimeStop = 0` means the app is using the mic now **[community; unverified on 26200, T2 probe]**.
- The source waits on `RegNotifyChangeKeyValue` for that subtree (event-driven, no polling).
- `in_call` = a mic is in use by an app in the user's **call apps** group (defaults: Teams, Zoom, Webex, Discord, Slack, and browsers *only while* `meeting_now` is true). Otherwise it is false.
- **Fallback** if the registry route fails the probe: `in_call` = `meeting_now` (calendar only), and the card says so.

### `ConnectivitySource : IEventSource<NetEvent> + IMetricSource` (I14)
- **Network List Manager** events give connectivity changes. `NetworkInformation.GetInternetConnectionProfile().GetNetworkConnectivityLevel()` = `ConstrainedInternetAccess` means a captive portal **[verified: learn.microsoft.com NetworkConnectivityLevel]**.
- Wi-Fi signal comes from `WlanRegisterNotification` (signal quality).
- **SSID / BSSID are not read.** On recent Windows 11 builds, reading them requires location permission **[unverified: build and behaviour, T2]**, and the card does not need them.
- Drops = transitions to offline, counted per hour and per day (series).
- **"Top upload process" is limited.** Per-process bytes need admin ETW or `GetPerTcpConnectionEStats`, which is elevated **[unverified, T2]**, and Tasktor never elevates (A3). U2 shows instead the **processes holding the most established TCP connections** (`GetExtendedTcpTable`, no admin), labelled honestly as "connections, not bytes".

### `FolderWatchSource : IEventSource<FileEvent>` (I12)
- One `ReadDirectoryChangesW` watch per chosen folder, non-recursive by default.
- **Debounce:** an event fires only when the file's size is stable for 2 s, it is not locked, and its name is not a partial download (`.crdownload`, `.part`, `.tmp`, `.partial`).
- At most 8 watched folders **[estimate]**.

### `AgentHookSource : ITableSource<SessionId, AgentSession>` (I1)
- Watches `agents\` and reads each `tasktor.agent/1` file (U1). Files older than their agent's PID lifetime are expired.
- **Reported** state wins over **estimated** state for the same pid.

### `BluetoothBatterySource : ITableSource<DeviceId, Battery>` (E8, limited)
- For **paired** devices, the PnP device property for Bluetooth battery level (the one Settings shows) **[community; unverified, spike]**.
- BLE devices also try the GATT Battery Service **[verified: E draft, MS Learn]**. HID devices that Windows owns may refuse it **[unverified]**.
- AirPods' separate left / right / case values are not exposed on Windows (E9, not possible).

### `CacheLocator` (I7; a resolver, not a source)
- Resolves each named cache from its tool's own config or environment variable first, then the documented default:
  - pip (`pip cache dir` / `%LOCALAPPDATA%\pip\Cache`);
  - npm (`npm config get cache` / `%LOCALAPPDATA%\npm-cache`);
  - Hugging Face (`HF_HOME` / `%USERPROFILE%\.cache\huggingface`);
  - conda (`pkgs_dirs`);
  - Gradle (`GRADLE_USER_HOME` / `%USERPROFILE%\.gradle\caches`);
  - Maven (`%USERPROFILE%\.m2\repository`);
  - NuGet (`NUGET_PACKAGES` / `%USERPROFILE%\.nuget\packages`);
  - Cargo (`CARGO_HOME`);
  - uv (`UV_CACHE_DIR`).
- Only paths that exist are shown. It never asks the tool to run unless that tool's cache path can only come from its CLI, and then only at report time, as a `StartWorker`-free plain `Process.Start` with a 5 s timeout **[decision for H: see OQ-S6]**.

## Features, row by row

### I1 AI agent watch (Module)
- **Rows:** one per running agent session, from the `agents` group (D-S3) and B's `JobGate` busy classifier.
  - Columns: agent, folder name (last segment of its working directory), state, for how long, CPU in cores.
  - Sort: needs-permission → waiting → working → idle.
- **Estimated** (no setup): busy = the process tree used CPU or spawned a child in the last 30 s (B's window).
- **Reported** (opt-in per agent): hook files, states `working` / `needs-permission` / `waiting` / `ended`.
  - Claude Code hooks map `UserPromptSubmit` → working, `Notification` (permission prompt) → needs-permission, `Notification` (idle) / `Stop` → waiting, `SessionEnd` → ended. Hooks receive `session_id`, `cwd` and `hook_event_name` on stdin **[verified: I draft, code.claude.com hooks]**.
  - Codex has `UserPromptSubmit` / `PermissionRequest` / `Stop` / `SessionEnd` behind `features.hooks`; its payload fields are not documented **[unverified, T2]**.
- **Q1 hook setup:** Tasktor shows the exact change to the agent's settings file and writes it **only on confirm**, with a `.bak` and a Remove button. It never edits a settings file silently.
- **Q2 permission toast:**
  - On by default: when a session has been `needs-permission` for **2 min** and its terminal window is not in front (B2.5's check).
  - Buttons: Show, Snooze 15 m.
  - A default rule (`agent-needs-permission`).

### I2 Overlap guard (Core rule, off)
`{ "id": "overlap", "when": { "group": "agents", "busy_count_above": 2 }, "for": "1m", "then": [ toast + badge ] }`. `busy_count_above` uses `JobGate`'s busy classifier, so idle sessions do not count. N is editable. No automatic action: the toast offers "Lower the newest" (reversible).

### I3 Meeting guard (Module)
- **Default rules (off until the Module is on):**
  - `in_call` → `pause refits` + `pause capture-content-worker` (cheap, on).
  - `in_call` → `set_priority below_normal` on opted-in groups (default none).
  - `undo: true`.
- B's OQ-B17 is satisfied: pausing jobs in a call is a rule the user turns on, never automatic by default.

### I4 Power guard (Core rules)
- `on_battery` → `pause refits` (**on**, cheap and expected).
- `on_battery` + opted-in group running → `efficiency` + `set_priority below_normal` (off).
- `on_battery` + opted-in group running → `cap_cores { max: 2 }` (off; **B3.5's core cap lives here**).
- "Release all caps" is an action on the power card. `undo` restores everything when plugged in.
- A8.2 still only warns; this is where acting lives (A's boundary).

### I6 Time per project (Module, needs T0 tracking)
- **Projects** are user labels over C's workflows (many workflows → one project): `state\projects.json` maps workflow id → project name.
  - Unmapped time shows as "Unassigned".
  - Mapping survives refits, because C keeps workflow ids stable (D-C12).
- **Card:** today, this week, and last week per project, from C9's **human** time.
- Export: `tasktor summary --by project` (U1's D5, the same numbers-only rule).

### I7 Space report (Core, report only)
- A **tab in A5's disk flyout** (A's proposal accepted).
  - A5 keeps volumes and generic biggest folders.
  - I7 shows named categories: each cache (D-S5 list), Downloads, `%TEMP%`, OneDrive files kept on this device, Recycle Bin size, and folders the user adds.
- **OneDrive kept on device** = files under the OneDrive root without `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / offline, i.e. hydrated **[verified: Windows file attribute docs; the attribute meaning for OneDrive, unverified, W1]**.- Scans run on request at BelowNormal through `FolderTreeSource`, within D-A3's ≤ 60 MB folder-scan budget. Results are cached on disk for 24 h.
- **Never deletes.**
  - Actions: Open in Explorer, and "Copy the tool's own cleanup command" (e.g. `pip cache purge`, `npm cache clean --force`) to the clipboard.
  - It never runs them. The user does (H's `run` stays a rules-only, click-only action).

### I11 Required-window watch (Module)
- The user picks a window from a list of current windows. Tasktor stores the **app id + a title pattern the user can edit** (a user-authored pattern, kept in `rules.json`).
- A `WinEventSource` lease for `EVENT_OBJECT_DESTROY` / `NAMECHANGE` / `SHOW` exists only while an I11 watch exists.
  - This is the same source F (U3) uses. Window events are **lease-driven, not owned by F's toggle**, so I11 works with F off.
  - A WinEvent hook is "in-context" vs "out-of-context". U2 uses out-of-context hooks; no DLL is injected **[verified: SetWinEventHook doc]**.
- **Default rule:** `window.missing` for 30 s → toast "Notebook tab closed?" with buttons Show app / Ignore today.

### I12 Folder watch with an action (Module)
- `FolderWatchSource` + a rule. The action is U1's `run`, declared in the same rule and offered **only as a toast button**.
- The example rule in U1's D-H6 (`new-pdf`) is the template.
- Commands may use `{path}` / `{file}` and Known Folder placeholders, never literal user paths in shipped rules.

### I13 Event mode (Module)
- An **event window**: start, end, name. Created by hand, or by tagging calendar events whose title contains a user-chosen marker (default `[focus]`).
- While active:
  - `state: event_mode` is true;
  - a `Countdown` block on the card;
  - default rules pause refits and the content worker;
  - optionally restore a saved layout (U3's I5; a no-op until U3 ships).
- B's `KindStats` "learned normal of recurring timings" is **not** used for I13 in v1. E11.2 uses the same `KindStats` shape per focus preset.

### I14 Connectivity watch (Module)
- **Card:** status (online / offline / captive portal), drops today and this week, Wi-Fi signal sparkline, top connection-holding processes.
- **Default rules:**
  - `captive_portal` → toast "Sign in to this network" with Open browser (launch is a click);
  - more than 3 drops in 10 min → toast once per hour.

### E1 Deadlines (Module)
- Sources: `CalendarSource` from the deadlines file and/or calendar events with a deadline marker (default: all-day events whose title starts with `Due`).
- **Card:** the next 5 deadlines as `Countdown` blocks. Severity is warn under 48 h and critical under 24 h.
- No toasts by default. A default rule "24 h before" is available (off).

### E2 Next event (Module)
- **Card:** next event with a live countdown, rest of today (`List`), urgency severity at 15 / 5 / 1 min.
- **Default rule:** `meeting_now` minus 1 min → toast with **Join** (opens the meeting URL) and Snooze.
- **No auto-open.** Opening a URL without a click would break U1's "rules never run anything unclicked" (D-H6).

### E8 Bluetooth batteries (Module, later, limited)
- **Card:** paired devices with battery %, low at 15%.
- Shipped as "limited": the card says when a device does not report its level.

### E10 Repo hygiene (Module, later; first-party plugin `tasktor.repos`)
- Folders the user picks, searched to depth 3 for `.git`.
- For each repo it runs `git status --porcelain=v2 --branch`. The `# branch.ab +N -M` line gives ahead / behind against upstream **[verified: git-status docs, porcelain v2]**, and the entry count gives dirty files.
- `git fetch` runs only if the user turns on "check remotes" (network, D-S2).
- **Card:** dirty, unpushed and behind counts per repo, plus Open folder.
- Requires git on PATH; otherwise the card says so.

## Architecture (builds on D-A5; no new interface)
```
 OS notifications ──► U2 sources (enqueue only) ──► logic loop: state + rule conditions ──► default rules (rules.json) ──► ActionRouter (U1) ──► B's / A's actions (reversible, undo)
 plugins (network) ──► plugin-data\<id>\*.json ──► CalendarSource                    └──► feature cards (A's blocks) ──► viewers (U4)
```
| Class | Kind | Implements / composes | Rows |
|---|---|---|---|
| `CalendarSource` | source | `ITableSource`; `IcsReader` (or Ical.Net), `RecurrenceExpander` | E1, E2, I3, I13 |
| `CallStateSource` | source | `IEventSource`; registry notify | I3 |
| `ConnectivitySource` | source | `IEventSource` + `IMetricSource`; NLM, WLAN | I14 |
| `FolderWatchSource` | source | `IEventSource`; `Debouncer` | I12 |
| `AgentHookSource` | source | `ITableSource`; directory watch | I1 |
| `BluetoothBatterySource` | source | `ITableSource` | E8 |
| `CacheLocator` | resolver | used by `SpaceReport` | I7 |
| `AgentWatch`, `MeetingGuard`, `PowerGuard`, `ProjectTime`, `SpaceReport`, `WindowWatch`, `FolderAction`, `EventMode`, `Connectivity`, `Deadlines`, `NextEvent`, `DeviceBatteries` | features | `Feature` + `ICardProvider`; rules from `rules.default.json` | each row |
| `FocusTimer` | feature | `Feature` + `ICardProvider`; presets from `config.json`; drives `EventWindow` | E11.1 |
| `FocusReview` | Core helper | reads C9 / C10 / `IdleSource` / C16 for a block; `KindStats` per preset; ≥ 5-block trust ramp | E11.2 |
| `NotesStore` | Core helper | `notes\` plain-text files; byte copy to a worker on save | E11.3 |
| `DataExporter` | Core | one writer per table (CSV + JSONL), `manifest.json`, the include flags; runs off-loop (D-A5 §7); confirm via `ActionRouter` | H16 (D-S7) |
| `ProjectMap` | Core helper | workflow id → project, persisted (byte copy to a worker) | I6 |
| `EventWindow` | record + helper | start / end / name, from the user or calendar markers | I13 |
| `tasktor.calendar`, `tasktor.repos` | first-party plugins (Python, bundled runtime) | U1 plugin contract | E1, E2, E10 |
| `cap_cores` | new reversible rule action | `IProcessControl` (Job Object CPU rate) | I4 (B3.5) |

### Time and space (U2's share)
| Operation | Naive | Design |
|---|---|---|
| "Am I in a call?" | poll audio sessions every few seconds | registry change notification; O(call apps) per change |
| Calendar | parse every ICS every minute | parse on file change / midnight; keep ≤ 200 instances for 14 days; countdowns computed on read |
| Folder watch | poll directory listings | `ReadDirectoryChangesW`; O(1) per event; 2 s stability debounce |
| Window watch | `EnumWindows` loop | WinEvent hooks only while a watch exists |
| Connectivity | ping a host | NLM events; zero traffic |
| Space report | rescan on each open | scan on request, 24 h cache on disk, top-N per level (A5) |
| Agent watch | parse command lines every sample | the `agents` group matched once per distinct exe (A's cache) + hook files on change |

1. `CalendarSource` + `RecurrenceExpander`
2. `CallStateSource`
3. `FolderWatchSource` + `Debouncer`
4. `AgentWatch`
5. `SpaceReport` + `CacheLocator`
6. `ProjectMap`
7. `FocusTimer` + `FocusReview`

## Open questions (probes, with working defaults)
| # | Question | Default | Checked |
|---|---|---|---|
| OQ-S1 | CapabilityAccessManager `LastUsedTimeStop = 0` = mic in use, on 26200, with change notifications | use it; calendar-only fallback | T2 |
| OQ-S2 | Ical.Net under Native AOT without trimming warnings | small in-repo reader if not | T2 |
| OQ-S3 | Bluetooth battery PnP property readable without admin | limited card | later |
| OQ-S4 | Codex hook payload fields | pass state only; ignore unknown fields | T2 |
| OQ-S5 | OneDrive hydrated = no `RECALL_ON_DATA_ACCESS` | yes | W1 |
| OQ-S6 | Running a tool's CLI to find its cache dir | **decided: allowed**, at report time only, 5 s timeout, no network | — |

## Changes for the catalog (merged at signing)
- **E11 reshaped:** focus timer with custom presets + cross-validation against tracking + quick notes; clipboard history cut.
- **I8–I10, E3 → U5.**
- **I5 → U3.**
- I-rows: "a private pack adds" stays as the pack's job.
- **U1 (H):**
  - the rule catalog gains the states and events in D-S1 and the `cap_cores` reversible action (D-S4);
  - G-net becomes "the agent never connects, with all Modules on" (D-S2);
  - new public contract `tasktor.calendar/1`;
  - `plugins-builtin\` in the install layout;
  - budget note "+ ≤ 1 MB with U2 Modules";
  - OQ-S6.
- **B:** the agent list is the `agents` group (D-S3); B3.5's core cap is `cap_cores` in I4; OQ-B17 is satisfied by I3's rule.
- **C:** `ActorAttributor` reads the `agents` group; `WinEventSource` window events are lease-driven, shared by I11 and F; `FocusReview` reads C9 / C10 / C16 per block (read-only; numbers only); T1's suppression list gains Tasktor's own processes (E11.3); `forget tracking` also clears `state\focus.json`.
- **U1 (H), additionally:** `notes\` in the store layout (kept by `forget all`); `settings export --include-notes`; `state\focus.json` cleared by `forget tracking`.
- **A:** I7 becomes a tab in A5's disk flyout.

## Consolidated at signing (2026-11-04)
U2 is signed. This file does **not** edit other sections. Each row below is a change for its owner. "Must" means U2 depends on it.

**U2 decisions in one place**
| Id | Decision |
|---|---|
| D-S1 | Each feature = source(s) + default rules + a `Feature` card; no new interface; new states and events are catalog rows |
| D-S2 | The agent process never connects; network Modules are first-party plugins (`tasktor.calendar`, `tasktor.repos`) that write `plugin-data` files the agent reads |
| D-S3 | One AI-agent list: the `agents` group in `rules.json` |
| D-S4 | Guards are default rules over existing reversible actions with `undo`; only "pause refits on battery" is on; app-affecting rules act only on ticked groups; new action `cap_cores` (B3.5) |
| D-S5 | Per-source privacy table: mic on / off, calendar in memory for 14 days, no SSIDs, file names only, repo counts only, ICS URLs encrypted in the plugin |
| D-S6 | Event- or lease-driven sources; ≤ +1 MB for the agent with all U2 Modules on |
| D-S7 | `tasktor export data`: CSV + JSONL per table + manifest + `tasktor_data` Python loader; raw titles and T1 content only with explicit flags and confirmation |
| E11 | Focus timer with custom presets, cross-validated against tracking (report only, ≥ 5 blocks per preset), quick notes; clipboard history cut |
| I1 | Hooks written only on confirm (diff, `.bak`, Remove); permission toast on after 2 min when the agent's window is not in front |

**U1 (H, signed): changes needed**
| # | Change | Kind |
|---|---|---|
| H-S1 | Rule catalog gains states `in_call`, `meeting_now`, `event_mode`, `offline`, `captive_portal`; events `file.created`, `window.missing`, `agent.state`, `net.dropped`; condition `busy_count_above` | must |
| H-S2 | New reversible action `cap_cores` (Job Object CPU rate / affinity) with `undo` and "Release all caps" | must |
| H-S3 | G-net tightens to "the agent process never connects, with every Module on" (D-S2); optionally move H13's update check into a first-party plugin | must (optional part: can) |
| H-S4 | Install layout gains `plugins-builtin\` (first-party plugins, off by default, same consent) | must |
| H-S5 | New public contracts `tasktor.calendar/1` and `tasktor.export/1` | must |
| H-S6 | Store layout gains `notes\` (kept by `forget all`) and `state\focus.json` (cleared by `forget tracking`); `settings export --include-notes` | must |
| H-S7 | New row H16 Export my data (D-S7); `export data` joins the action catalog (read class; the include flags confirm like destructive) | must |
| H-S8 | D-H10 budget note: "+ ≤ 1 MB with U2 Modules" | must |

**A:** I7 is a tab in A5's disk flyout (A's proposal, accepted). *(must)*
**B:**
- the known-agent list is the `agents` group (D-S3);
- B3.5's core cap is `cap_cores` in I4;
- OQ-B17 is satisfied by I3's opt-in rule;
- B's `jobs_runs` / `job_kinds` tables are in the D-S7 export. *(must)*

**C:**
- `ActorAttributor` reads the `agents` group;
- window events are lease-driven, shared by I11 and F;
- `FocusReview` reads C9 / C10 / C16;
- T1 suppression covers Tasktor's own windows;
- C14 is served by the D-S7 export;
- `forget tracking` also clears `focus.json`. *(must)*

**U3 (windows):** `WinEventSource` window events are lease-driven (I11 holds a lease without F on); I13 / E11 call U3's saved-layout restore when it exists.
**U4 (surfaces):** cards for every U2 row; Settings → Your data → Export; the focus timer card and notes editor in the flyout; the "needs permission" toast; the hook-setup diff dialog.
