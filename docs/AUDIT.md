# Feature audit (2026-11-07)

Every live row of `docs/design/01-catalog.md` against the code on `claude/cool-euler-qjz6mk`.

- **Built**: code and tests exist, the Linux suite and the Windows CI build pass. Most still need the real-PC check listed
  in `docs/PROGRESS.md` → "Needs Windows verification".
- **Partial**: some of the row exists; the "Missing" column says what does not.
- **Not started**: no code beyond contracts or a stub.

Cut, folded and "not possible" rows (A4, A9, A10, C13, E4–E7, E9, F1, F3, F5, G5, G7) are left out.

## A. System monitor

| Row | Feature | Status | Missing |
|---|---|---|---|
| A1 | CPU, RAM, GPU, disk, network + sparklines, 60-min history | Built | real-PC check of GPU per adapter |
| A2 | Processes grouped by app, apps tree | Built | — |
| A3 | Per-process actions and guards (Close first, End, frozen guard) | Built | — |
| A5 | Disk guard, biggest folders | Built | — |
| A6 | Pressure alerts (RAM, commit, CPU, disk) | Built | — |
| A7 | Dev ports | Built | — |
| A8 | Battery | Built | — |

## B. Long-job watch

| Row | Feature | Status | Missing |
|---|---|---|---|
| B1–B4 | Job rows, silent / stuck / finished, priority actions, progress files | Built | — |
| B5 | Tasktor's own long tasks as jobs | Built | — |
| B6 | What Tasktor learned (Learned page, Jobs tab) | Built | — |

## C. Workflow tracking and learning

| Row | Feature | Status | Missing |
|---|---|---|---|
| C1–C4 | Passive capture, browser domain, privacy gate, four levels | Built | C2 is "limited" by design per browser |
| C5, C6, C8 | Features, HDBSCAN model, gates, adaptive refit | Built | real data on a PC to pass the gates |
| C7 | Auto-named clusters; rename, merge, "That's wrong" | Built | Learned page → Workflows: Rename, Merge into |
| C9, C10 | Current workflow, time per workflow, switches per day | Built | — |
| C11, C12 | Unusual episode, prediction | Later (spec) | — |
| C14 | Export + Python loader | Built | — |
| C15 | Custom models | Later (spec) | — |
| C16 | Actor attribution | Built | keyboard hook attached (2026-11-09); live check of `agent:` / `injected` runs on a PC |
| C17 | Key runs, password / secure-field suppression | Built (layout check passed 2026-11-09) | keyboard hook attached after the US-International dead-key check; live checks (password fields, UAC, German / French layouts) on a PC |
| C18 | Screen text: UIA snapshots + browser text | Built (UIA check passed 2026-11-09) | browser text wired and recorded; UIA source (focus, debounced cached walks, terminals through `TextPattern`, click targets) attached (#8), windows that do not answer skipped; live check with real apps still to do; OCR fallback (C18.4) is T2 |
| C19 | Pointer: path, clicks with target, scroll, dwell, visible windows | Partial | pointer shaping (≤ 10/s), clicks, wheel, recorder gating, activity documents, async click-target resolution (100 ms budget, off the loop), per-activity features kept in the index (C19.3), `HookDevices` + hook latency benchmark (#6, passed 2026-11-09: p99.9 ≤ 31 µs); UIA hit-test resolver attached (#8); mouse hook attached (2026-11-09; live check with real clicks still to do); native visibility producer done (#7), timing borderline (p95 0.82–1.3 ms against 1 ms) |
| C20 | Media: SMTC + audio sessions, side tracks | Built (tone check passed 2026-11-09) | browser video wired; SMTC now-playing and WASAPI audible attached (#9); SMTC with a real player still to check |
| C21 | Content index (FTS5 + embeddings), "Search my activity" | Built, mostly; app-forget PC check passed 2026-11-09 | encrypted SQLCipher index; activities now close in-session (30 s away / 1 h cap); forget clears persisted and open-memory content (73 / 62 removed, 0 matches after stop); live search and site-forget checks remain; capture export sources missing; reopen web pages built, files/windows missing; 999 975-byte embedding table smoke-tested, owner-labelled G8 pending |
| C22 | CLI command log (PSReadLine, OSC 133, shell children) | Built; fixture PC checks passed 2026-11-09 | vault: 2 history rows, 1 redacted, 0 raw tokens/prior lines; exclusion and rewrite passed; capture-down refusal exit 2. Interactive PSReadLine/PS5 and capture export remain unverified. Terminal-marks preview built (no exit codes); agent-command live check remains |

## D. CLI

| Row | Feature | Status | Missing |
|---|---|---|---|
| D1 | `tasktor status`, `snapshot.json` | Built | — |
| D5 | `tasktor summary` | Built | — |

## E. Glance modules

| Row | Feature | Status | Missing |
|---|---|---|---|
| E1, E2 | Deadlines, calendar next event | Built (plugin `tasktor.calendar`) | — |
| E8 | Bluetooth device batteries | Built (limited by Windows) | — |
| E10 | Repo hygiene (`tasktor.repos`) | Built | its timeout was invalid until 34c64ea; recheck on a PC |
| E11 | Focus timer + validation against tracking | Built (Windows check pending) | timer, presets, review; notes editor in the flyout (edits the note files itself; text never crosses the pipe) |

## F. Window management

| Row | Feature | Status | Missing |
|---|---|---|---|
| F2 | Window switcher (Alt+`, thumbnails, search) | Built; partial PC checks 2026-11-09 | module start/stop, opening, quick tap, app view, 125% fit, frozen badge and two-Enter resume passed; unexpected search characters observed (exact keys/cause unconfirmed). MRU/latency, cycle/restore, Esc/click-away, 150%, consent/settings, workflow/paging, filtered thumbnails and Alt+Tab replacement remain unverified; 0 switcher-named files in fixture store |
| F4 | "On another desktop" badge, bring here | Built (badge); bring here off | badge in the switcher; spike W2 failed 2026-11-09 (`MoveWindowToDesktop` on another process's window: E_ACCESSDENIED), so `windows.bring_here` stays off (D-W7) |
| F6 | Multi-monitor / per-monitor DPI | Built (requirement) | real-PC checks |
| F7 | `state.fullscreen` | Built | — |

## G. Surfaces

| Row | Feature | Status | Missing |
|---|---|---|---|
| G1 | Widgets-board widget | Dropped (owner, 2026-11-09: no MSIX) | `Tasktor.Widget` stays a stub |
| G2 | Flyout → shelf, command line, deep window | Built (Windows check pending) | the command line's dimmed desktop is a layered click-through window (none in high contrast); dialogs: none left |
| G3, G4 | Taskbar button, overlay bar | Later (spec) | — |
| G6 | Tray icon, glance tooltip, menu | Built | — |
| — | Toasts | Built (tray balloons) | real toasts with buttons dropped with MSIX (owner, 2026-11-09) |

## I. Generalized tailored features

| Row | Feature | Status | Missing |
|---|---|---|---|
| I1 | AI agent watch | Built | hook setup / removal from the card, diff shown in the confirm |
| I2–I4 | Overlap, meeting, power guards | Built | — |
| I5 | Workflow presets | Built, mostly | create from open windows with the launch review; create, review and edit; missing: learned-workflow suggestions (L2) |
| I6, I7, I11, I14 | Projects, space report, window watch, connectivity | Built | I11's picker UI |
| I13 | Event mode | Built | live on / off without a restart (deliberately left, see Interpretations) |

## H. Platform

| Row | Feature | Status | Missing |
|---|---|---|---|
| H1–H3, H6, H10, H14–H16 | Permissions on use, local only, store, log, rules, self-use counts, processes, export | Built | — |
| H4 | Fluent look, theme, Narrator names | Built | real-PC check with Narrator |
| H5 | Budgets | Built (gates) | measured budgets on a PC |
| H7 | Tests + CI + gates | Built | — |
| H8 | **MSIX** + portable zip | Built (zip only; opt-in logon entry) | owner chose no MSIX (2026-11-09); settings.start_with_windows and CLI on/off manage HKCU Run, reconcile moved paths, uninstall removes entry; check instances never touch Run. Settings > General switch still missing; owner enables from installed build |
| H9 | Public repo docs | Built | README antivirus section to check |
| H11 | Plugin contract + authoring tools | Built | — |
| H12 | Releases, SHA256SUMS | Built | — |
| H13 | Update check | Later (spec) | — |


## UI restoration checkpoint (2026-11-10)

- The uncommitted deep-window/System patch translates the approved neutral shell and System layout to WinUI. System's selected tab uses a short accent underline, and expandable resource headers use the same 64-DIP height as the other resource rows. The command launcher is widened toward the reference. Existing cards, pins and guarded actions remain the data/action sources.
- The native x64 Release build passed after the tab and row adjustments. Native dark preview inspection confirmed the underline and consistent resource row heights; exact logical-width parity, the latest launcher change, light theme, narrow/maximized layout, focus and menu stability, and populated process fixtures remain to be checked. The isolated preview has an empty process table and does not prove populated app interactions.
- The System page still rebuilds on snapshot updates; preserving focus, open menus and confirmations through refresh remains open. Other deep pages and glance, dialogs, switcher, extension and Widgets restoration remain open.
- The owner reopened Windows Widgets scope in addition to the shelf. The portable no-MSIX decision remains historical; the provider is still a stub, and package identity and real-host validation remain open. No package installation or Windows setting change was made.


- Follow-up: the native x64 Release build passed with 0 warnings/errors after the launcher and deferred process-body changes. Four focused AppsTree tests passed; these test ordering and action targets, not native focus. The dark native preview showed the widened launcher, tab underline and consistent resource row heights. The isolated fixture passed its Release build and real-client protocol probe; a controlled synthetic snapshot reproduced loss of focus from an expanded app group, while a confirmation retained its safe-default focus. The refresh fix and populated native action verification remain open.


## System refresh checkpoint (2026-11-11)

- Implemented an in-place System reconciliation path with deferred structural changes during interaction and current-action validation. The process tree and resource expansion state remain backed by the existing snapshot and guarded action contracts.
- Native x64 Release build passed with 0 warnings and errors. With the isolated synthetic fixture, a tick updated CPU and app memory while the app expander retained focus and expansion; a simulated PID creation-time change updated visible and accessible app names while focus remained. Temporary diagnostic logging was removed.
- Native stale process context-menu invocation remains unverified because the permitted headless tool could not open the menu. Exact logical-width matching, light theme, narrow and maximized layouts, interaction/performance behavior under larger process feeds, and the complete restoration remain open. The fixture is synthetic and uses its own pipe and data directory.


## Home restoration checkpoint (2026-11-11)

- Implemented a responsive Home composition using the existing live shelf sentence, alerts, pinned cards and their actions, recent undo actions, and system history pulls. Two columns stack below 680 content DIPs; fixed-width history charts still need a width pass.
- Native x64 Release build passed with 0 warnings and errors. The isolated Home preview launched, but computer-use approval timed out; no visual, narrow-layout or native action verification is claimed. Home still rebuilds on snapshot changes, so interaction stability needs checking.


## Jobs restoration checkpoint (2026-11-11)

- Implemented a responsive Jobs page using snapshot job states, guarded row actions, learned durations and additional watcher cards. The mock process picker and notification toggle are omitted pending a supported UI/action contract.
- Native x64 Release build passed with 0 warnings and errors. An isolated Jobs preview was launched; native visual, narrow-layout and refresh-time interaction checks have not been performed. Jobs still rebuilds on snapshot changes.


## Activity restoration checkpoint (2026-11-11)

- Implemented responsive Activity layout using the real local-calendar-day summary pull, workflow and project cards, existing card actions and pins, and command search. Unsupported timeline segments, longest stretch, actor split, naming action and dedicated switch history remain omitted.
- The initial native build exposed a C# local-name collision and dependent XAML compiler errors; after renaming the local, native x64 Release built with 0 warnings and 0 errors. An isolated Activity preview was launched; native visual, narrow, search and refresh interaction checks remain open.


## Learned restoration checkpoint (2026-11-11)

- Implemented Workflows, Jobs and Deep capture views with independent workflow and job-learning pulls, existing workflow rename/merge dialogs and card actions, and the Settings capture-consent path. Unsupported mock controls and quality figures are omitted.
- Native x64 Release build passed with 0 warnings and errors; an isolated Learned preview was launched. Native visual, narrow, consent and action checks remain open. Learned retains the snapshot-refresh exemption, so external updates may require a redraw or tab change.


## Focus restoration checkpoint (2026-11-12)

- Implemented a full-width current timer and responsive preset/recent and local-notes columns, retaining existing Focus actions and the local notes store. Unsupported mock toggles and progress values are omitted.
- Native x64 Release build passed with 0 warnings and errors. The focused Home/Learned/Focus model tests passed 24/24. An isolated Focus preview launched, but the screenshot showed a different foreground window and the accessibility state was unavailable; native visual, narrow, timer action and editor focus/caret checks remain open.


## Windows restoration checkpoint (2026-11-12)

- Implemented responsive preset and switcher columns, preserving the existing preset save/edit/delete/arrange/apply/undo paths. Switcher settings remain editable in Settings and the Windows page shows their live state and a Settings link. Unsupported FancyZones status/actions and mock diagrams are omitted.
- Native x64 Release build passed with 0 warnings and errors; an isolated Windows preview was launched. Native visual, narrow, navigation and preset action checks remain open. Windows still redraws on snapshots.


## Your data restoration checkpoint (2026-11-12)

- Implemented responsive local-data and export/start-over columns using existing scoped Forget actions, two-step confirmations, export options and capture card. Start over is described as affecting all eligible categories; settings, notes and presets remain intact. Unsupported mock controls and counts are omitted.
- Native x64 Release build passed with 0 warnings and errors; an isolated Your data preview was launched. Native visual, narrow, checkbox focus, export and Forget interaction checks remain open.


## Settings restoration checkpoint (2026-11-12)

- Arranged the existing General, switcher and Modules controls beside Rules, Plugins and Appearance in responsive columns. Existing validation, action handlers and consent defaults remain in place. Unsupported mock switches were omitted.
- Native x64 Release build passed with 0 warnings and errors; an isolated Settings preview was launched. Native visual, narrow, consent and asynchronous redraw/focus checks remain open.


## Shelf restoration checkpoint (2026-11-12)

- Removed the sentence width cap, placed noun actions and confirmation above the existing single timeline, and adapted narrow action/link layout. Existing controller, focus-loss, lifetime and capture/privacy paths remain in place; safe confirmation focus is set when confirmation first opens.
- Native x64 Release build passed with 0 warnings and errors; 38 focused shelf tests passed. An isolated shelf preview was launched. Native visual, narrow scrolling, confirmation focus and snapshot-interaction checks remain open.


## Command restoration checkpoint (2026-11-12)

- Retained the approved 640-DIP maximum, bounded the surface to the work area, added result scrolling and selection visibility, and prevented Enter in the input from affirming a pending destructive confirmation. The explicit confirm button remains the affirmative path; Keep receives focus on entry.
- Native x64 Release build passed with 0 warnings and errors; 22 focused command model/controller tests passed. An isolated command preview was launched. Native sizing, keyboard and confirmation behavior, dimming and high contrast remain unverified.


## Dialog restoration checkpoint (2026-11-12)

- Added a scoped theme-aware style based on the native ContentDialog template and routed the existing dialogs through it. Existing content, validation, consent scopes and button defaults are unchanged.
- Native x64 Release build passed with 0 warnings and errors; 21 focused settings/data/consent model tests passed. Runtime resource resolution and native visual, narrow, keyboard, consent and high-contrast checks remain open.


## Switcher restoration checkpoint (2026-11-12)

- Updated visual paint to shared light/dark tokens and Segoe UI Variable Text, high-contrast system colors, DPI-scaled text/selection, and a small-cell selection fallback. Existing input, hooks, session guards, DWM thumbnail registration and activation were not intentionally changed.
- Native Release build and x64 Native AOT publish passed with 0 warnings/errors; 12 shell tests and 14 focused session/feed/Alt-Tab tests passed. Real theme changes, high contrast, DPI, small-cell selection and thumbnail composition remain unverified.


## Extension popup restoration checkpoint (2026-11-12)

- Styled the existing popup with neutral light/dark surfaces, Segoe Variable text, wrapped action rows and forced-colors/focus styles. Existing IDs, messaging, site/tab actions and private-profile opt-in remain in place; manifest and permissions were not changed.
- All 9 extension tests passed. Browser-host visual, keyboard, forced-colors and private-gating UI checks remain open.

## Windows Widgets draft checkpoint (2026-11-12)

- Implemented a draft Win32 COM provider and optional development package manifest for System, Jobs and Workflow aggregate glances. No names or deep links are included in the cached cards. The portable release format is unchanged.
- The provider x64 Release build passed with 0 warnings and 0 errors, and six synthetic formatter/activity tests passed. The package has not been made, installed or exercised in Windows Widgets. Picker screenshots, branding assets, identity and signing remain open.
- Independent source audit found that the COM provider has no release-triggered shutdown and that deactivation can race with an in-flight pull or host update. Provider lifecycle, host registration, real data behavior, actions, accessibility and visual fidelity remain unverified; this is not a completed widget surface.

## Verification checkpoint (2026-11-12)

- Non-mutating filtered solution format verification passed. The first filtered build failed because the isolated preview core locked Debug outputs; after verifying its executable and isolated pipe/data arguments, that preview core was stopped and the build passed with 0 warnings and errors.
- Filtered solution tests passed 1,204/1,204, worker pytest passed 122/122, and extension tests passed 9/9. The separate Widget synthetic tests passed 6/6 and its x64 Release build passed. These checks do not verify native layout, Windows Widgets registration, COM lifetime, or UI interaction.

## Widget subscription follow-up (2026-11-12)

- Membership changes now invalidate the previous activity generation, cancel an in-flight history pull, and wake the refresh loop. Reactivating the same instance invalidates work from its earlier activation.
- Eight synthetic Widget tests passed and the x64 Release provider build passed with 0 warnings/errors. A final check-to-UpdateWidget race remains, as does the missing COM release-triggered shutdown; neither host behavior nor visual appearance has been verified.
- The synthetic System preview exposed CPU, Memory and app rows through accessibility. Screenshot capture returned an unrelated foreground window, so the same-width native visual comparison remains open.
