# Tasktor — U4 Surfaces (feature spec)

**Status:** **signed**. D-G1–D-G9 decided; D-G6 as changed by the user (hideable edge line, capture blocks by time, app and site / tab, hideable pages); OQ-G1 Win+Alt+T, configurable; OQ-G2 System, Jobs, Workflow. Open items are probes only (G-W0a–e, G3).
**Reference low end:** i7-1355U, 12 GB RAM, Iris Xe, one monitor, Windows 11 26200.

Conventions: **[verified: source]** = checked against Microsoft docs; **[unverified]** = a claim to be proven in the named spike; **[community]** = third-party source. **S / M / L** = Widgets-board small / medium / large.

## What U4 starts with (signed elsewhere; cite, don't redesign)
- **The card model is A's D-A5, not the old G draft's.** A card = ordered **blocks** + semantic **hints** (severity, priority, unit, trend, icon key, accent role). Blocks: `Metric`, `Sparkline`, `Bar`, `Gauge` (series); `Table`, `List`, `Progress` (table); `Tree`; `Alert`, `Badge` (event); `Countdown`, `Actions` (cross). `Media` is unused since music was cut. A new block kind is a project-level change. **Size fitting is the renderer's job**, by priority. `IBlockRenderer` lives only in viewer hosts and is owned here.
- **U1 D-H2:** the snapshot is `state` + `cards`; card blocks hold literal values or **bindings** (JSON Pointer into `state`); renderers resolve bindings. Plugins send literal values only.
- **U1 D-H3 / D-H4:** viewers read `snapshot.json` or subscribe on the pipe; every button sends `act` to `ActionRouter`; destructive actions are two-step with a 30 s confirm token. Surfaces never act on their own.
- **U1 D-H9:** the tray icon lives in the agent; the flyout and widget are their own processes; `tasktor-shell` (U3) draws window UI.
- **U1 asks:** renderers resolve bindings; the tray menu is built from `ActionCatalog`; the flyout ≤ 80 MB (OQ-H5); plugin cards use the block subset with a `source` badge; destructive buttons follow D-H4.
- **A's open questions:** OQ8 (portable build toasts need an app identity) and OQ9 (flyout exits ~30 s after hide vs kept warm).
- **The card asks** (each spec's "Consolidated at signing"):
  - **B:** Jobs card with Pause / Resume, "paused 12 m", learned/default badge, ETA and "longer than usual" on M/L; the B6 **Learned** view; Settings "Learn from my jobs" / "Forget everything learned".
  - **C:** workflow card with an actor split; "Search my activity" (C21); a "What Tasktor learned" view with Jobs / Workflows tabs; the **T1 indicator** (tray icon + a screen-edge line).
  - **U2:** a card for every U2 feature; Settings → Your data → Export; the focus timer card and notes editor; the "needs permission" toast; the hook-setup diff dialog.
  - **U3:** the Presets card; the preset review screen; the switcher's look (shared tokens); Settings → Windows; the rule-consent dialog for `arrange`.
- **The G1 widget design** (picker set, hybrid actions, update flow) was drafted 2026-11-03 and is carried below, updated to blocks and bindings.

## Verified platform facts

**Widgets board (G1)**
- Sizes small / medium / large, declared in the manifest. Recommended max touch targets: **S 1, M 3, L 4** [verified: widgets-interaction-design, widgets-design-fundamentals].
- Guidance: one primary page; no second-level pages, no scrolling to reach primary info; a call to action "should launch the associated app or website instead of implementing the action in the widget itself" [verified: widgets-interaction-design]. Pagination dots are a documented pattern [verified: same].
- Layout: 16 px margin, 48 px header/attribution area, 4 px gutters, Segoe UI type ramp (Caption 12/16 … Title 28/36); separate light and dark designs per size [verified: widgets-design-fundamentals].
- Templates are Adaptive Cards; Microsoft's samples use card `version` 1.5 with `$when` on `$host.widgetSize` to switch per size [verified: implement-widget-provider-win32]. Action.Execute arrived in AC 1.4 [verified: adaptive-cards schema]. The host forwards Action.Execute to `OnActionInvoked` with the `verb`; Action.OpenUrl does not call the provider [verified: widgets-create-a-template]. When `$host.isUserContextAuthenticated` is false, only Action.OpenUrl should be offered [verified: same].
- Lifecycle: `CreateWidget` on pin, `DeleteWidget` on unpin, `Activate`/`Deactivate` per widget bracket the window when the host wants fresh content; "the time window between Activate and Deactivate may be small". Providers may push at any time but should weigh battery. `OnWidgetContextChanged` currently fires only on resize [verified: implement-widget-provider-win32]. **No documented update-rate limit** found; the 1–2 s in the Frame is a guess for W0 to measure [unverified].
- Provider registered via MSIX manifest (`com:ComServer` + `uap3:AppExtension Name="com.microsoft.windows.widgets"`); Developer Mode is a prerequisite for local dev; sample uses WinAppSDK 2.5.1 [verified: implement-widget-provider-win32]. Customization ("Customize widget" in the … menu) exists since WinAppSDK 1.4 [verified: same].
- Web (HTML) widgets: EEA-only (Frame, verified 2026-11-03).


**Pinned taskbar button (G3)**
- `ITaskbarList3` still documents SetOverlayIcon (badge), SetProgressValue/State, ThumbBarAddButtons; min client listed as Windows 7, no Windows 11 statement either way [verified: ITaskbarList3 reference]. Win11 behaviour of each [unverified → G3 spike].

**Tray icon (G6)**
- `Shell_NotifyIcon` tooltips (`szTip`) hold at most 128 characters including the terminator [verified: NOTIFYICONDATA]. A plain Win32 popup menu fits a Native AOT agent with no UI framework (U1 D-H9).

## Cross-cutting decisions

### D-G1 One renderer stack: resolve, fit, draw
Every surface runs the same three steps, so a card looks like itself everywhere.
1. **Resolve.** `BindingResolver` replaces each `{"bind": "/state/…"}` with the value from the latest `state`. A missing pointer renders "—" and logs once; it never throws.
2. **Fit.** `CardFitter` keeps the highest-priority blocks that fit the target size. Ties keep the provider's order.
| Target | Budget (units: `Metric`/`Badge`/`Countdown` = 1, `Sparkline`/`Bar`/`Gauge`/`Progress` = 1, a `Table`/`List` row = 1, `Tree` = flyout only) | Touch targets |
|---|---|---|
| Tray tooltip / CLI one-liner | the card's top `Metric` + its severity word | 0 |
| Widget S | 2 units | 1 (the whole card) |
| Widget M | 5 units | 3 |
| Widget L | 9 units | 4 |
| Flyout, CLI `status` | everything | all |
   `Actions` count against touch targets, not units; the fitter keeps the highest-priority actions and the rest stay reachable in the flyout.
3. **Draw.** One `IBlockRenderer` per host: `AdaptiveCardRenderer` (widget), `FlyoutRenderer` (WinUI controls, live charts), `TextRenderer` (CLI `status`, plain text). The tray tooltip uses a small Core helper, `GlanceText`, so the agent carries no renderer (A: `IBlockRenderer` lives only in viewer hosts).
- **Common rules in every renderer:** stale cards (past `stale_after_s`) grey out with "as of hh:mm"; plugin cards show a `source` badge with the plugin's name; severity is shown by **icon + word**, never colour alone; a destructive action is labelled "End…" and opens the two-step confirm (D-H4).

### D-G2 Flyout: WinUI 3, exits 30 s after it hides, measured
- **Tech:** WinUI 3 on the Windows App SDK, self-contained so the portable build runs it unpackaged. Native AOT if spike G-W0b shows it works with WinUI 3 **[unverified]**; otherwise trimmed JIT.
- **Opens from:** a tray click, the global hotkey, a toast button or widget deep link (`tasktor://open?card=<id>&row=<n>`), and `tasktor open`.
- **Placement:** anchored above the tray like Windows' own flyouts, 400 × 640 DIPs; "Pop out" turns it into a normal resizable window that remembers its size.
- **Lifetime (OQ9):** it exits **30 s after it hides**, so Tasktor's idle cost stays the agent's ≤ 15 MB. G-W0b measures cold start on the reference laptop; **if a cold open takes > 600 ms**, the default becomes "stay warm for 5 min after each use". A Settings switch "Keep the window ready" keeps it warm always (≤ 80 MB, OQ-H5).
- **Data:** subscribes on the pipe while visible; pulls full tables (A's process table, C's search) only for the page on screen; unsubscribes when hidden.

### D-G3 Flyout pages (the views every unit asked for)
A left rail with nine pages. Pages for Modules that are off are hidden.
| Page | Contents | Asked by |
|---|---|---|
| **Home** | alert strip (active alerts, newest first); pinned cards (default: System, Jobs, Workflow; the user pins any card); "Undo" for actions in the last 30 min | A, H6 |
| **System** | CPU / RAM / GPU / disk / network charts (A1, 60 min history); apps table with groups → processes (`Tree`), per-row actions (A3); disk tab + **Space report** tab (I7); Dev ports tab (A7, Module) | A, U2 |
| **Jobs** | running and recent jobs, progress, ETA, Pause / Resume, frozen state, "Watch as job" | B |
| **Activity** | today by workflow (C9), switches (C10), actor split (C16); **Search my activity** (C21, T1 only); project time (I6) | C, U2 |
| **Learned** | tabs **Jobs** (B6: per kind, guardrail log, Relearn, Forget) and **Workflows** (C: clusters, gates, rename, merge, Forget) | B, C |
| **Focus** | timer + presets (E11.1), last blocks and preset comparison (E11.2), notes editor (E11.3) | U2 |
| **Windows** | presets list, Apply / Arrange / Edit, the preset review screen (launch arguments shown before saving) | U3 |
| **Your data** ("What Tasktor knows") | each store with tier, size, age and its Forget button (D-H8); Export (D-S7); self-use counts (H14); capture status | U1, U2, C |
| **Settings** | General (hotkeys, start with Windows, update check); Modules (on / off per Module); Rules (rules.json list, enable, edit in an editor, last fired); Plugins (consents, D-H7); Windows (switcher hotkey, Alt+Tab override, "this desktop only"); Learning ("Learn from my jobs", tracking, capture with its consent); Appearance | all |

**Dialogs (modal, in the flyout):** two-step confirm (D-H4); plugin consent (D-H7); capture consent (C, D-C22); hook-setup diff with `.bak` and Remove (I1); preset review (U3); rule consent for `arrange` (U3 OQ-W2); export confirm with the plain-text and cloud-folder warnings (D-S7).

### D-G4 Tray icon (G6)
- **Icon states:** normal; warn; critical (top active alert's severity); paused (tracking paused); **recording** (T1 capture on: the icon gets a red ring, D-G6). High-contrast variants for each.
- **Tooltip:** `GlanceText` of the top-severity card, else System: "CPU 34% · RAM 71% · train.py 52%" (≤ 127 characters).
- **Left click:** open / hide the flyout. **Right click:** a menu built from `ActionCatalog` rows marked `surface: tray` (U1 ask):
  - Open Tasktor
  - Pause tracking ▸ 30 min / 1 hour / Until I resume
  - Pause capture ▸ 15 min / 1 hour / until tomorrow / until I resume, and "Don't capture this app" (only while T1 is on; D-G6)
  - Focus ▸ the presets (only with E11 on)
  - Window presets ▸ the presets (only with U3 on)
  - Snooze alerts for 1 hour
  - Settings
  - Quit Tasktor (resumes frozen jobs first, B)

### D-G5 Widgets board (G1): the 2026-11-03 design, on blocks and bindings
- **Picker set (decided 2026-11-03):** Tasktor System (`core.system`), Tasktor Jobs (`core.jobs`), Tasktor Workflow (`core.workflow`), and **Tasktor card** (any card, chosen through Customize widget; pin it several times). Plugin cards reach the board only through Tasktor card.
- **Template / data split:** `AdaptiveCardRenderer` turns the fitted blocks into an Adaptive Card 1.5 template with `$when` on `$host.widgetSize`, and the resolved bindings into its data JSON. Structure changes rebuild the template; samples change only the data.
- **Actions (decided 2026-11-03, hybrid):** reversible actions run in place (Action.Execute → provider → pipe → `act`); destructive ones say "End…" and deep-link to the flyout with the confirm step armed; card body → flyout at that card.
- **Updates:** a pipe subscription only between Activate and Deactivate; at most one `UpdateWidget` per widget per 2 s (G-W0a measures what the host honours).
- **Agent not running:** "Tasktor isn't running" with Start. **Lock screen** (`isUserContextAuthenticated` false): glance only, no Action.Execute, no app or job names.
- MSIX only (the portable build has no widget).

### D-G6 T1 indicator and capture blocks
**Requirement:** allow hiding the edge line, and let users explicitly block capture at certain times and for certain apps, windows and browser tabs.

**Indicator.**
- **Tray (always on while T1 runs):** the recording ring (D-G4) and a tooltip line "Recording typing and screen text · Pause". This part cannot be turned off, so there is always a visible sign that capture is on.
- **Screen-edge line (on by default, can be hidden):** a 2 px, click-through, topmost layered window along one edge of every monitor, drawn **by `tasktor-capture.exe`**, so it exists exactly while capture runs (if capture crashes the line goes with it; no other process can draw a false "off"). Settings → Learning → Capture: show / hide, colour, edge. Hiding it asks once ("The tray ring stays; you can turn the line back on here").
- Over **exclusive** fullscreen nothing can draw on top, so the tray ring is the indicator there **[unverified: confirm in G-W0e]**.
- The capture process gains a tiny Win32 window and still no network code (G-capture-net unchanged).

**Capture blocks (new, the user's request).** The user can block deep capture directly, without editing lists:
| Block | How the user sets it | Effect |
|---|---|---|
| **For a while** | tray → Pause capture ▸ 15 min / 1 hour / until tomorrow / until I resume; flyout Capture card | capture stops (the process idles); the line and ring show "paused until hh:mm" |
| **On a schedule** | Settings → Capture → Schedule: weekly time ranges ("never 9–5 on weekdays", "only 19:00–23:00") | outside allowed times capture is paused; shown on the Capture card |
| **This app / window** | tray → "Don't capture this app" (acts on the foreground app); flyout → Your data → Capture → Excluded apps; Learning page per-app rows | the app joins C's exclude list (C3 rule 3): runs store only length and duration, no screen text |
| **This site / tab** | the browser extension's popup: "Never capture this site" and "Pause for this tab" (until the tab closes); the flyout's Excluded sites list | host joins the exclude list; a paused tab sends nothing until it closes |
| **Anything already recorded** | Search my activity → select → Delete; or "Forget the last 15 min / hour / today" on the Capture card | deletes from the vault and index (C's `forget capture` with a time range) |

- Every block is a **reversible action** in the catalog (`capture_pause { until }`, `capture_exclude { app | host }`, `capture_tab_pause { tab }`) with `undo` where it makes sense, and each shows in the Capture card's "Blocks" list with Remove.
- Rules may *add* blocks (for example, "pause capture while in a call") but never remove one; only the user lifts a block (a rule lifting a privacy block would be a silent way to turn recording back on).
- **Hiding pages:** Settings → Appearance lets the user hide any flyout page except Home, Your data and Settings (so the forget and export controls are always reachable).

### D-G7 Notifications: one notifier, and an identity for the portable build
- `ToastNotifier` (the agent's `INotifier`) sends every toast: ≤ 3 buttons (D-H6); each button is an `act` with origin `toast`; one tag per rule, so a repeat **replaces** the old toast instead of stacking; Windows' Do Not Disturb and Focus decide when toasts appear.
- **Identity:** MSIX has one. The **portable build** registers an app user model ID for the current user the first time a toast is needed, and `tasktor uninstall` removes it. Microsoft documents notifications for unpackaged apps **[unverified: which registration route works from a Native AOT process without the Windows App SDK; spike G-W0c]**. If none works, toasts fall back to tray balloons.

### D-G8 Look and access (H4)
- **Fluent:** Mica on the flyout, Acrylic on menus, rounded corners; light / dark / accent follow Windows; reduced motion turns chart animation off; high contrast uses system colours only.
- **Shared tokens:** `Tasktor.Theme` (spacing, type ramp, severity roles, icon set) is used by the flyout and `tasktor-shell` (U3's switcher). The widget uses the host's ramp and colours.
- **Accessible by construction:** every block maps to a UI Automation name built from its semantics ("CPU, 34 percent, rising, normal"); every action is keyboard-reachable; charts have a table equivalent (Tab into a chart → its values). G-W0d checks Narrator on the widget's Adaptive Cards.

### D-G9 Other surfaces
| Row | Verdict |
|---|---|
| G3 pinned taskbar button | **later**, after a spike: badge (overlay icon) for the top severity and a progress fill for the top job, if Windows 11 still honours them |
| G5 dashboard tab next to Discover | **cut**: EEA-only and not reachable for most users |
| G7 UI inside the taskbar | not possible (already recorded) |
| G4 overlay bar | U3 D-W10: later, experimental, off |
| CLI | `tasktor status` prints cards through `TextRenderer` at flyout size; `--json` prints the snapshot untouched (D1) |

## Card catalog
One row per card. Blocks are listed in priority order; the fitter cuts from the bottom. "Owner" designs the content; U4 only fixes the id and the fitting.
| Card id | Owner | Blocks (priority order) | S shows | Actions |
|---|---|---|---|---|
| `core.system` | A1, A2.7 | Metric CPU · Metric RAM used % · Metric GPU · verdict line (`Badge`) · Sparkline CPU · Sparkline RAM · Table top 3 apps · Metric disk free · Metric network | CPU + RAM, or the verdict when one exists | Open |
| `core.apps` | A2, A3 | Table app groups (CPU, RAM, GPU) · Tree (flyout) | top app | Close · Lower · Efficiency · End… |
| `core.disk` | A5 (+ I7 tab) | Metric free on C: · Bar per drive · Badge low-space | free GB | Biggest folders · Open in Explorer |
| `core.battery` | A8 (laptops) | Metric percent · Badge charging · Metric time left | percent | — |
| `core.jobs` | B | Table jobs (Progress, ETA, badges: paused / frozen / learned / longer than usual) · Metric running count | count + longest | Pause · Resume · Open log · End… |
| `core.workflow` | C | Metric current workflow (+ confidence) · Bar actor split (C16) · List today's top 3 · Metric switches today | workflow name | Pause tracking |
| `core.guards` | U2 (I3, I4, I13) + B | List of active guards ("Paused refits: on battery", "train.py capped to 4 cores") | count active | Undo · Release all caps |
| `core.capture` | C (T1) | Badge recording / paused until · List active blocks · Metric records today | "Recording" or "Paused" | Pause ▸ · Don't capture this app · Forget last hour |
| `module.ports` | A7 | Table port · app · address · Badge open-to-network | count | Kill… |
| `module.agents` | U2 I1 | Table sessions (state: working / waiting for permission / done) | waiting count | Show window |
| `module.projects` | U2 I6 | Table this week by project · Bar | top project | — |
| `module.windows_watch` | U2 I11 | List watched windows · Badge missing | missing count | Show app |
| `module.folders` | U2 I12 | List recent folder actions | last action | Undo |
| `module.connectivity` | U2 I14 | Metric state · Sparkline drops per hour | online / offline | — |
| `module.deadlines` | U2 E1 | Countdown list | next deadline | Open |
| `module.next_event` | U2 E2 | Countdown · Badge in meeting | next event | Join (link) |
| `module.batteries` | U2 E8 | List devices with Bar | lowest device | — |
| `module.focus` | U2 E11 | Countdown · Badge preset · List pinned notes | time left | Start · Pause · Skip · Stop |
| `module.presets` | U3 I5 | List presets | — (no S) | Apply · Arrange |
| `plugin.<id>.<card>` | plugins (D-H7), e.g. `tasktor.repos` (E10), `tasktor.calendar` | the plugin's blocks (subset) + `source` badge | plugin glance | the plugin's own actions |

## Architecture (classes on A's types)
| Class | Assembly / process | Kind | Responsibility |
|---|---|---|---|
| `BindingResolver` | Hosts.Common | helper | JSON Pointer → value; "—" on a missing pointer |
| `CardFitter` | Hosts.Common | helper | priority fitting per target (D-G1) |
| `SnapshotClient` | Hosts.Common | pipe client | hello / subscribe / pull; reconnects |
| `ActionInvoker` | Hosts.Common | helper | `act` with origin; carries the confirm token through the two-step dialog |
| `Tasktor.Theme` | Hosts.Common | resources | shared tokens for the flyout and `tasktor-shell` |
| `AdaptiveCardRenderer` | Widget | `IBlockRenderer` | blocks → AC 1.5 template; resolved values → data |
| `WidgetProvider` + `WidgetSession` | Widget (COM) | host | create / delete / activate / deactivate; coalesced updates |
| `FlyoutRenderer` | Flyout | `IBlockRenderer` | block → WinUI control; live charts; UIA names |
| `FlyoutShell` + nine pages + seven dialogs | Flyout | UI | D-G3 |
| `TextRenderer` | CLI | `IBlockRenderer` | `tasktor status` |
| `GlanceText` | Core | helper | tooltip text from the top card's first `Metric` |
| `TrayIcon` (exists, U1) | agent | UI | icon states and menu from `ActionCatalog` |
| `ToastNotifier` | Windows / agent | `INotifier` | toasts, tags, portable identity |
| `CaptureIndicator` | `tasktor-capture` | UI | the edge line (D-G6) |

**Interfaces:** none new; `IBlockRenderer` (A's eighth, owned here) gets its three implementations.

## Time and space
| Path | Cost |
|---|---|
| Resolve + fit one card | O(blocks); a few µs |
| Snapshot change → widget | coalesced to ≤ 1 update per widget per 2 s; data JSON only unless structure changed |
| Flyout open | cold start target ≤ 600 ms **[G-W0b]**; ≤ 80 MB while open; 0 MB 30 s after hiding (default) |
| Charts | redraw at the sample rate (1 Hz), paused when the flyout is hidden or minimized |
| Widget provider | ≤ 60 MB while activated (D-H9) |

## Open questions and spikes
| # | Question | Spike / owner |
|---|---|---|
| G-W0a | Widget host's honoured update rate; template-vs-data update cost | W0 (MSIX, Developer Mode needs the user's OK) |
| G-W0b | WinUI 3 + Native AOT works; flyout cold start and memory on the reference laptop | W0 |
| G-W0c | Toast identity for the portable build from a Native AOT agent | W0 |
| G-W0d | Narrator reads the widget's Adaptive Cards usefully | W0 |
| G-W0e | Capture's edge line over borderless and exclusive fullscreen | with C's A1 probes |
| G3 | Windows 11 behaviour of overlay icon / progress / thumb buttons | later spike |
| OQ-G1 | Flyout hotkey | **decided: Win+Alt+T by default, configurable** | — |
| OQ-G2 | Home's default pins | **decided: System, Jobs, Workflow** | — |

## Changes for the catalog (for signing)
- G1 → D-G5; G2 → D-G2 / D-G3; G6 → D-G4; G3 → later after a spike; G5 → cut; G7 unchanged; H4 → D-G8.
- A's OQ8 → D-G7; OQ9 → D-G2.

**Asks for other units:** (consolidated below)
- **U1 (H):** new reversible actions `capture_pause { until }`, `capture_exclude { app | host }`, `capture_tab_pause { tab }`, and `forget capture --since`; the rules grammar lets rules add capture blocks but not lift them; `ActionCatalog` rows gain `surface` tags (`tray`, `widget`, `toast`) and `act` carries `origin`; budget rows for the flyout (≤ 80 MB open, 0 after 30 s) and widget; `tasktor open` and `tasktor uninstall` (portable identity cleanup) in the CLI; the capture process gains a tiny UI window (G-capture-net unchanged).
- **C:** the T1 indicator is drawn by `tasktor-capture` itself, and the edge line can be hidden (tray ring cannot) (D-G6); **capture blocks**: pause until a time, a weekly schedule, per-app exclude from the foreground app, per-site exclude and per-tab pause from the browser extension popup, and `forget capture` with a time range (last 15 min / hour / today); rules may add blocks but never lift one; the Activity and Learned pages host C's views.
- **A:** OQ8 and OQ9 are answered here; `core.system` / `core.apps` / `core.disk` / `core.battery` ids are fixed.
- **B, U2, U3:** card ids fixed as in the catalog; no behaviour change.

## Sources
- learn.microsoft.com/windows/apps/design/widgets/widgets-design-fundamentals (ms.date 2026-10-23)
- learn.microsoft.com/windows/apps/design/widgets/widgets-interaction-design (2026-10-23)
- learn.microsoft.com/windows/apps/develop/widgets/implement-widget-provider-win32 (2026-10-23)
- learn.microsoft.com/windows/apps/develop/widgets/widgets-create-a-template
- learn.microsoft.com/adaptive-cards/schema-explorer/action-execute
- learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-itaskbarlist3

## Consolidated at signing (2026-11-04)
U4 is signed. This file does **not** edit other sections. Each row below is a change for its owner. "Must" means U4 depends on it.

**U4 decisions in one place**
| Id | Decision |
|---|---|
| D-G1 | One render path: `BindingResolver` → `CardFitter` (priority fitting; S 2 / 1, M 5 / 3, L 9 / 4) → one `IBlockRenderer` per host; severity by icon + word; destructive = "End…" + two-step |
| D-G2 | Flyout: WinUI 3; Win+Alt+T (configurable); anchored above the tray, can pop out; exits 30 s after hiding, warm 5 min if a cold open exceeds 600 ms (A's OQ9) |
| D-G3 | Nine flyout pages + seven dialogs covering every unit's views; Home pins System, Jobs, Workflow |
| D-G4 | Tray: five icon states, `GlanceText` tooltip, menu from `ActionCatalog` rows tagged `tray` |
| D-G5 | Widget board: System, Jobs, Workflow + Tasktor card; template / data split; hybrid actions; ≤ 1 update per widget per 2 s; lock screen glance only |
| D-G6 | T1 indicator: tray ring always; edge line drawn by `tasktor-capture`, on by default, hideable; capture blocks by time, schedule, app, site and tab; forget by time range; rules may add blocks, never lift them; pages hideable except Home, Your data, Settings |
| D-G7 | One `ToastNotifier`; ≤ 3 buttons; repeats replace; portable identity registered on first toast, balloons as fallback (A's OQ8) |
| D-G8 | Fluent look, shared `Tasktor.Theme` with the switcher, Narrator names and chart tables |
| D-G9 | G3 later after a spike; G5 cut; G7 not possible; CLI `status` via `TextRenderer` |

**U1 (H, signed): changes needed**
| # | Change | Kind |
|---|---|---|
| H-G1 | New reversible actions `capture_pause { until }`, `capture_exclude { app \| host }`, `capture_tab_pause { tab }`; `tasktor forget capture --since <15m\|1h\|today>` | must |
| H-G2 | Rules grammar: a rule may **add** a capture block but never lift one (only the user lifts a privacy block) | must |
| H-G3 | `ActionCatalog` rows gain `surface` tags (`tray`, `widget`, `toast`); `act` carries `origin` (`widget` / `flyout` / `toast` / `tray` / `cli` / `rule`) | must |
| H-G4 | D-H10 budget rows: flyout ≤ 80 MB open and 0 MB 30 s after hiding (default); widget provider ≤ 60 MB while activated, ≤ 1 update per widget per 2 s | must |
| H-G5 | CLI gains `tasktor open [--card <id>]` and `tasktor uninstall` (portable: removes the toast identity and the Run key) | must |
| H-G6 | `tasktor-capture.exe` gains a tiny Win32 window for the edge line; G-capture-net is unchanged | must |

**A:** OQ8 is answered by D-G7 and OQ9 by D-G2; card ids `core.system`, `core.apps`, `core.disk`, `core.battery` are fixed. *(must)*
**B:** card id `core.jobs`; the Jobs page and the Learned → Jobs tab host B's views as asked. *(no behaviour change)*
**C:** *(must)*
- the edge line is drawn by `tasktor-capture` itself; it is on by default and hideable, while the tray ring cannot be turned off;
- capture blocks: pause until a time; a weekly schedule; "Don't capture this app" on the foreground app; "Never capture this site" and "Pause for this tab" in the browser extension popup; forget by time range (last 15 min / hour / today);
- rules may add blocks but never lift one;
- card id `core.capture` shows active blocks;
- the Activity and Learned → Workflows pages host C's views (C9, C10, C16, C21).

**U2, U3:** card ids fixed as in the catalog; no behaviour change.

## Amended 2026-11-06: edge, sentence, command (the maintainer's decision)
After mockup rounds, the maintainer replaced the flyout-as-dashboard with a three-layer surface. The rows below are **amended from U4** and win over D-G2, D-G3, D-G4, D-G5 and D-G8 where they differ; everything they do not mention stands (the renderer stack D-G1, the capture blocks of D-G6, toasts D-G7, the card catalog as the data model).

| Decision | Text |
|---|---|
| D-G10 *(amended from U4)* | **At rest: the edge.** No window. A 6 px click-through strip along the top edge of the taskbar on the primary monitor, drawn by the shelf host: the last hour as a grain of ticks (left = an hour ago, right = now, height = load); the stretch where a card has a caution or critical severity is drawn at full ink. Same no-network and Native-AOT rules as the agent's viewers. |
| D-G11 *(amended from U4)* | **Opened: the shelf** (replaces D-G2's flyout as the glance surface). Hover or click on the edge or the tray icon grows the edge upward into a full-width shelf in the taskbar's own colour: one **sentence** built from the top cards' verdicts (a calm machine is one sentence), whose nouns are the controls (apps, jobs, blocks: tapping one unfolds its actions inline), over one **timeline strip** of the hour with events written on it and a dotted forecast past "now". Destructive actions confirm **inside the sentence** with the consequence spelled out; the safe choice is the default. Height follows content; it hides on focus loss. |
| D-G12 *(amended from U4)* | **Command line** (Win+Alt+T, configurable): one input over the dimmed desktop, no panel; the current verdict, suggested verbs, then results across apps, jobs, settings and the user's own activity (C21 search). Enter runs the highlighted action through `act` (origin `command`). |
| D-G13 *(amended from U4)* | **The deep window.** D-G3's nine pages stay, in a normal resizable window opened from "Everything Tasktor knows" on the shelf, the tray menu or `tasktor open`; it is for detail (apps tree, Learned, Your data, Settings), not for glancing. Its dialogs keep D-H4's two-step rule. |
| D-G14 *(amended from U4)* | **Look** (replaces D-G8's Fluent cards): neutral surfaces plus the Windows accent for what is in motion only (progress, the open state, links); caution and critical appear only as a small mark beside their word; red means capture is recording and nothing else. No cards, borders or shadows inside Tasktor's own surfaces; rows are separated by alignment and hairlines. Type: Segoe UI Variable (Display for the sentence, the command input and titles; Text for everything else), so nothing is bundled; icons are Fluent UI System Icons. Tokens live in `Tasktor.Hosts.Common/Theme/ThemeTokens.cs` and the viewers' `Themes/Tasktor.xaml`, kept equal by a test; every text token reaches 4.5:1 on every surface. High contrast uses system colours only; reduced motion stops the grain from animating. |
| D-G15 *(amended from U4)* | **Tray, widgets, toasts keep Windows' frames.** The tray icon is the edge in miniature (three waves) with a small shape for severity and a red ring while recording; hover opens the shelf; the right-click menu is Windows' own, led by the sentence. Widgets and toasts are drawn by Windows; their content is the sentence plus a strip, never modules. |
| D-G16 *(amended from U4)* | **Capture's indicator** (refines D-G6): `tasktor-capture.exe` draws its own 2 px line along the **top** edge of every monitor, so it exists exactly while recording; on the shelf, capture speaks as a sentence that names its blocks, each removable in place. |

**Changes this needs elsewhere:** `act` origins gain `command` and `shelf`; D-H10 gains a shelf-host budget row (target ≤ 30 MB while open, 0 MB 30 s after it hides, like the flyout it replaces); M14's WinUI work becomes the shelf host + the deep window instead of the 400 × 640 flyout.
