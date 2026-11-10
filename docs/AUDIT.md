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
