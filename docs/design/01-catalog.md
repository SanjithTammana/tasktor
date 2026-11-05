# Tasktor — feature catalog

> Every feature agreed or carried over in planning, 2026-11-03. Phases and components refer to
> `00-frame.md`. The background process is `tasktor-core`.
>
> **Reoriented 2026-11-03 for public distribution.** Tasktor is a downloadable Windows app anyone can
> run and change. Every feature now has a **tier**:
> - **Core**: ships, on by default, works on any Windows 11 PC with no setup and no account.
> - **Module**: ships, off by default; the user turns it on (it may ask for a permission, a location, a folder or a login).
>
> Status: **planned** (has a phase), **later**, **limited** (works partially on Windows), **not possible** (with reason).
> Nothing in the public repo names the user's machine, projects, courses or contacts; examples in public docs are generic.


## A. System monitor — **signed 2026-11-04**; detail: `features/A-system-monitor.md`
| # | Feature | Tier | Phase | Status |
|---|---|---|---|---|
| A1 | CPU, RAM (used % first), GPU per adapter, disk, network, with sparklines + 60-min history (A1.6) and editable warn/critical thresholds (A1.7) | Core | W1 | planned |
| A2 | Processes grouped by app (tool processes count under the launching app); GPU column; "System / services" bucket; one-line "why is it slow" verdict on the small card (A2.7); grouping rules editable | Core | W1 | planned |
| A3 | Per-process actions and guards (absorbs A4): **Close (graceful) first**, End / End tree (two-step), low priority, Efficiency mode, open location, open in Task Manager; own-user, non-protected only; Tasktor **never elevates** | Core | W1 | planned |
| A5 | Disk guard: free space (tick), threshold `max(10%, 15 GB)`, biggest folders on request, open in Explorer | Core | W1 | planned |
| A6 | Pressure alerts: RAM + commit (on, waits on Windows' low-memory event); CPU and disk saturation rules (off) | Core | W1 | planned |
| A7 | Dev ports: listeners, owner, kill, port-conflict lookup, "open to network" badge | Module | T2 | planned |
| A8 | Battery: percent, charging, time left, unplugged-under-load warning (throttling stays in I4) | Core (laptops only) | W1 | planned |
| A4 | → folded into A3 | | | folded |
| A9 | → folded into A1.3 / A2.3 (WDDM 2.0 is a Win11 minimum) | | | folded |
| A10 | CPU temperature | — | — | cut (a Pack may add it) |

## B. Long-job watch — **signed 2026-11-04**; detail: `features/B-long-job-watch.md`
B is **adaptive** (D-B9): each policy has a learned per-kind value, the fixed rule as fallback, and guardrails; trust ramps over 5–20 good runs; learned data is numbers only, keyed by a hashed kind; learning on by default.
| # | Feature | Tier | Phase | Status |
|---|---|---|---|---|
| B1 | Job rows: windowed gate (≥ 30 s CPU within 2 min, age ≥ 60 s, sticky; pin and progress files bypass it); CPU in cores; exit codes; hogging badge; learned per-kind rows (B1.8–B1.10) | Core | W1 | planned |
| B2 | Silent (adaptive, 10-min fallback), stuck-busy, and finished / failed toast (only when the job's window is not in front) | Core | W1 | planned |
| B3 | Priority and stop actions via A3: tree-wide Lower / Efficiency / Restore, auto-restrain rule (off), End tree, **B3.7 Pause job** (freeze or polite pause, crash-safe resume); core cap moves to I4 | Core | W1 | planned |
| B4 | Progress files `jobs/<id>.json` schema v1 (OSC 9;4 state names, optional heartbeat), public contract owned by WS0; Python helper + `tasktor job` CLI | Core | W1 | planned |
| B5 | Tasktor's own long tasks as jobs (refits L2; folder scan W1) | Core | W1 / L2 | planned |
| B6 | Job kinds: what Tasktor learned (view, forget, relearn) | Core | W1 | planned |

## C. Workflow tracking and learning — **signed 2026-11-04**; detail: `features/C-workflow-learning.md`
C has two tiers, and both are Modules that are **off by default**. **T0** is core tracking: event-driven, with no key or mouse hooks, and it fits inside the D-A3 budget. **T1** is deep capture. It has its own consent screen and an always-visible indicator, and it runs as a separate `tasktor-capture` process (≤ 40 MB, ≤ 0.5% CPU) that records only the current user and has no network code. A shown workflow always falls back in this order: workflow, then activity cluster, then app. It is labelled learned or default. Online assignment is in C#; batch fits run in a bundled embeddable Python 3.13 worker that is never resident. Thresholds are pre-registered in `prereg.json`.
| # | Feature | Tier | Phase | Status |
|---|---|---|---|---|
| C1 | Passive capture: foreground app + title (hook scoped to the foreground process), idle, process start/stop from B's 60 s scan; window open/close only when F runs; notifications off (MSIX only); opt-in screen (C1.7) | Module T0 (asked on first open of the workflow card, never silently) | A1 | planned |
| C2 | Browser domain via cached UIA, read off-thread; per-browser cost guardrail; per-browser private-window detector, otherwise app-only | T0, inside C1 | A1 | limited (unverified per browser) |
| C3 | Privacy gate: pause, three rule strengths, Private bucket, redaction list, 30-day raw retention; **"What Tasktor knows"** view with Hide a word (C3.6) | T0, core of C1 | A1 | planned |
| C4 | Four levels: segment, activity, workflow window, session | C1 | L1 | planned |
| C5 | Features in C# (df ≥ 3 → LSA 64 → PCA 16); HDBSCAN workflow model, k-means baseline, NMF report-only; "a job was running" feature from B | C1 | L1 | planned |
| C6 | Label-free gates (G0 data gate, G6b information beyond app via a null model); C6.4 human check (report only) | C1 | L1 | planned |
| C7 | Auto-named clusters; rename, merge; **That's wrong** feedback (per-cluster guardrail) | C1 | L1 | planned |
| C8 | Refit with adaptive cadence (7 → 28 days) + drift-triggered early refit; member-overlap stable ids; fit cap 10k; kill-on-close Job Object; rollback | C1 | L2 | planned |
| C9 | Current workflow + time per workflow (= X1), human time by default | C1 | L2 | planned |
| C10 | Workflow switches / interruptions per day | C1 | L2 | planned |
| C11 | Unusual-episode flag (anomaly vs the user's normal clusters) | C1 | later | later |
| C12 | Next-app / next-workflow prediction | C1 | later | later (only if it beats a simple baseline) |
| C13 | → moved to H14 | | | moved |
| C14 | Export CSV / JSONL + read-only Python API to the store (Parquet later) | C1 | L1 | planned |
| C15 | Custom models: drop a Python model script into `models/custom/`; the worker trains and runs it beside the built-in clustering | Module | later | later |
| C16 | Actor attribution: human / agent:<app> / cli:<shell> / injected:unknown, from injected-input flags + B's agent list + process tree; never guesses a name | Module T1 | A1 | planned |
| C17 | Key stream as a queue of key runs (typed text after edits, chords); password / secure-field / excluded-app suppression + redaction before any write; 7-day raw | T1 | A1 | planned |
| C18 | Screen text: event-driven UIA snapshots with stable refs + line diffs; browser via a companion MV3 extension (native messaging, no CDP); OCR only as a gated last resort | T1 | A1 | planned |
| C19 | Pointer and attention: 10 Hz path, clicks with target element, scroll, dwell, visible windows and screen share | T1 | A1 | planned |
| C20 | Media and concurrent activity: now-playing (SMTC) + audio sessions; ▶ line on the workflow card | T1 | A1 | planned |
| C21 | Content index: FTS5 + chunk embeddings (gate G8 vs TF-IDF), "Search my activity" Flyout, reopen from a hit; per-record AES-GCM vault, `forget --all` | T1 | L1 | planned |
| C22 | CLI command log (PSReadLine history, OSC 133 marks, shell child command lines); feeds C16 | T1 | A1 | planned |

| # | Feature | Tier | Phase | Status |
|---|---|---|---|---|
| D1 | → U1 (signed): `tasktor status [--json] [--cards]` + `snapshot.json` (state + cards) + current-user pipe; actions go through the one `ActionRouter` (D-H2–D-H4) | Core | A0 | planned |
| D5 | → U1 (signed): `tasktor summary`, numbers only, from C's episodes under rule 5b | Core | later | later |

## E. Glance modules — remaining rows **signed 2026-11-04 in U2**; detail: `features/U2-sources-guards.md`
| # | Feature | Tier | Source | Status |
|---|---|---|---|---|
| E1 | Deadlines countdown from a plain file or any .ics calendar | Module | file / ICS URL | planned (T2) |
| E2 | Calendar: next event + live countdown, rest-of-day drawer, urgency as it nears, optional auto-open of Teams/Zoom/Meet/Webex links | Module | ICS URL first; Outlook/Google login later | later |
| E4 | Music: now playing + controls | — | — | cut |
| E5 | Synced lyrics | — | — | cut (music) |
| E6 | Spotify Up Next / playlists | — | — | cut (music) |
| E7 | Weather | — | — | cut |
| E8 | Bluetooth device batteries (BLE Battery Service) | Module | Windows.Devices.Bluetooth | limited |
| E9 | AirPods left / right / case separately | — | — | not possible (not exposed on Windows) |
| E10 | Repo hygiene: dirty or unpushed repos under folders the user picks | Module | git on demand | later |
| E11 | Focus timer with custom presets, cross-validated against workflow tracking (report only); quick notes; clipboard history cut (U2) | Module | — | planned (T2; validation L2) |

## F. Window management — **signed 2026-11-04 in U3**; detail: `features/U3-windows.md`
| # | Feature | Mechanism | Status |
|---|---|---|---|
| F1 | (cut as a taskbar feature, U3 D-W1) every window of an app with live thumbnails → the switcher's app view (F2) | DWM thumbnails in `tasktor-shell` | cut → F2 |
| F2 | Window switcher: Alt+` by default, opt-in Alt+Tab override; thumbnails, search, app / workflow views, job / frozen / desktop / admin badges | `tasktor-shell` (RegisterHotKey; opt-in LL hook that stores nothing) + `WindowList` | planned (U3, T2) |
| F3 | (cut, U3 D-W1) zones and tiling are PowerToys FancyZones; a preset may set a FancyZones layout via its CLI | `FancyZonesBridge` | cut → FancyZones |
| F4 | "On another desktop" badge; "bring here" only if spike W2 passes | IVirtualDesktopManager (public only, D-W7) | planned (U3, T2) |
| F5 | (cut, U3 D-W7) no desktop switching; choosing a window on another desktop activates it | — | cut |
| F6 | Multi-monitor, per-monitor DPI: a requirement on all window code (device-path monitors, DIP rects) | `MonitorMap` | requirement (U3 D-W8) |
| F7 | `state.fullscreen` (none / borderless / exclusive / presentation) for rules and surfaces | `FullscreenSource` (SHQueryUserNotificationState + monitor cover check) | planned (U3, T1) |

All of F is a Module group.

## G. Surfaces — **signed 2026-11-04 in U4**; detail: `features/U4-surfaces.md`
| # | Surface | Status |
|---|---|---|
| G1 | Widgets-board widget: System, Jobs, Workflow + a configurable "Tasktor card"; S / M / L by priority fitting; hybrid actions; MSIX only (U4 D-G5) | planned (W0, W1) |
| G2 | Own flyout (WinUI 3): Win+Alt+T (configurable), tray, toast, widget deep link; nine pages + seven dialogs; exits 30 s after hiding (U4 D-G2, D-G3) | **v1** |
| G3 | Pinned taskbar button: badge, progress fill | later, after a Windows 11 spike (U4 D-G9) |
| G4 | Overlay bar in the taskbar's free space | later, experimental, off (U3 D-W10) |
| G5 | Dashboard tab next to Discover | cut (EEA-only; U4 D-G9) |
| G6 | Tray icon: five states, glance tooltip, menu from the action catalog incl. capture pause / don't-capture-this-app (U4 D-G4, D-G6) | **v1** |
| G7 | UI inside the taskbar itself | not possible (Windows 11 removed deskbands, toolbars and app taskbar customisation) |

## I. Tailored features, generalized (accepted 2026-11-03, all 14) — generic rows **signed in U2** (`features/U2-sources-guards.md`); I5 → U3; I8–I10 → U5
Each ships as a generic feature; anything user-specific belongs in a pack.
| # | Generic feature (ships) | Tier | a pack may add | Cost |
|---|---|---|---|---|
| I1 | AI agent watch: running coding agents (Claude Code, Codex, Cursor, … by an editable process list), busy vs idle | Module | — | low |
| I2 | Overlap guard: warn when more than N processes of a chosen group run at once | Core rule (off by default) | — | low |
| I3 | Meeting guard: while a call app is in a call (or a calendar event is running), lower opted-in jobs' priority and pause refits | Module | — | low-med |
| I4 | Power guard: on battery, pause refits and throttle opted-in jobs | Core | — | low |
| I5 | Workflow presets: save open windows or a learned workflow; apply by click / hotkey / CLI / focus Start (move + launch); rules may only `arrange` (undo, one-time consent) (U3 D-W5) | Module | — | med |
| I6 | Time per project: the user maps clusters to project names; weekly totals | C1 | — | low once L2 exists |
| I7 | Space report (report only, never deletes): Downloads, %TEMP%, package caches (pip, npm, Hugging Face, conda, Gradle, Maven, NuGet), OneDrive files kept on device, user-added folders | Core | — | med |
| I11 | Required-window watch: warn if a chosen window or tab title disappears | Module | — | low |
| I13 | Event mode: countdown, saved layout, paused refits for a time window | Module | — | low |
| I14 | Connectivity watch: Wi-Fi drops, captive-portal detection, top upload process | Module | — | med (per-process upload is limited) |

## H. Platform, quality and distribution — **signed 2026-11-04 as U1**; detail: `features/U1-platform.md`
| # | Feature | Status |
|---|---|---|
| H1 | First run asks for nothing; each permission only when its feature is turned on | planned |
| H2 | Local-only, no account, no telemetry, no cloud sync | planned |
| H3 | Store root `%LOCALAPPDATA%\Tasktor` (Known Folders, MSIX excluded-directory fix); `config.json` with migration + `.bak`; export / import / reset; `tasktor forget learned / tracking / capture / all` (D-H8) | planned |
| H4 | Fluent look: Mica/Acrylic, rounded corners, light/dark and accent follow Windows, reduced motion, high contrast | planned (U4 D-G8: shared theme, Narrator names, chart tables) |
| H5 | One budget table for every process (D-H10): agent idle ≤ 15 MB (≤ 17 with T0) / ≤ 0.1%; viewer open ≤ 30 MB; capture ≤ 40 MB / ≤ 0.5%; flyout ≤ 80 MB; plugins ≤ 256 MB per run; checked by `SelfMonitor` and CI | planned |
| H6 | Local log without personal data (`Sensitive<T>`), verbose mode auto-reverts in 24 h, `tasktor log bundle` | planned |
| H7 | Tests + CI: xUnit / pytest, Linux for Core, Windows for OS code + AOT; gates G-net, G-capture-net, G-schema, G-redact, G-perf, G-aot (D-H11) | planned |
| H8 | Two builds: **MSIX** (widget + toasts + start at logon) and **portable zip** (agent + tray + flyout, no install) | planned |
| H9 | Public repo: README (privacy promise, antivirus warnings section), Apache-2.0 LICENSE, CONTRIBUTING, docs for plugins / rules / contracts, issue templates | planned |
| H10 | Rules file `rules.json` (JSON with comments, schema autocomplete): grammar v1, reversible actions only, `undo`, toasts ≤ 3 buttons, `run` only from a clicked toast button (D-H5, D-H6) | planned |
| H11 | Plugin contract v1: `plugin.json`, output = a fixed subset of A's blocks, one process per run in a Job Object, consent pinned to hashes, packs (D-H7) | planned |
| H12 | Distribution: GitHub Releases from CI tags, **unsigned**, T1 included; SHA256SUMS; Store / winget later (D-H12) | planned |
| H13 | Update check: notify only, off by default; `.appinstaller` for sideloaded MSIX | later |
| H14 | Self-use counts: local-only usage counts of Tasktor itself, never sent anywhere (from C13) | planned |
| H15 | Process topology + supervision: agent with the tray icon, flyout, widget, CLI, capture, nmh, workers, plugins; capture restarts ≤ 3 / hour (D-H9) | planned |
| H16 | Export my data: `tasktor export data` / Settings → Your data → Export; CSV + JSONL per table + manifest + `tasktor_data` Python loader; raw titles and T1 content only with explicit flags + confirmation (U2 D-S7) | planned |
