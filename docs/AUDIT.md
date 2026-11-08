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
| C16 | Actor attribution | Built (logic) | needs the keyboard hook attached |
| C17 | Key runs, password / secure-field suppression | Built (logic, fail closed) | **hook not attached** until the layout check on a PC |
| C18 | Screen text: UIA snapshots + browser text | Partial | browser text wired and recorded; **UIA source** uncommitted on the Codex machine |
| C19 | Pointer: path, clicks with target, scroll, dwell, visible windows | Partial | the ring carries mouse events and the visibility logic exists; **no pointer pipeline** (clicks, dwell, scroll) and nothing drains mouse events |
| C20 | Media: SMTC + audio sessions, side tracks | Partial | browser video wired; **SMTC / WASAPI source** uncommitted on the Codex machine |
| C21 | Content index (FTS5 + embeddings), "Search my activity" | Built, mostly | encrypted index (SQLCipher) + search in the command line; missing: reopen links, forget by app / host, embeddings (G8) |
| C22 | CLI command log (PSReadLine, OSC 133, shell children) | Built (C22.1) | PowerShell history tailed, reconciled with typed runs, scrubbed and recorded; missing: OSC 133 offer (C22.3), agent-run commands from B's scan (C22.4) |

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
| E11 | Focus timer + validation against tracking | Partial | **notes editor** (blocked by a privacy / contract question) |

## F. Window management

| Row | Feature | Status | Missing |
|---|---|---|---|
| F2 | Window switcher (Alt+`, thumbnails, search) | Partial | filter / MRU logic exists in Core; **`tasktor-shell` is a stub**: no window, thumbnails, hotkey or opt-in Alt+Tab hook |
| F4 | "On another desktop" badge, bring here | Partial | `VirtualDesktopInfo` exists; no switcher to show it |
| F6 | Multi-monitor / per-monitor DPI | Built (requirement) | real-PC checks |
| F7 | `state.fullscreen` | Built | — |

## G. Surfaces

| Row | Feature | Status | Missing |
|---|---|---|---|
| G1 | Widgets-board widget | **Not started** | `Tasktor.Widget` is a stub; needs MSIX (H8) |
| G2 | Flyout → shelf, command line, deep window | Built, mostly | the command line's **dimmed desktop**; dialogs: none left; Settings → **Windows** (waits for F2) |
| G3, G4 | Taskbar button, overlay bar | Later (spec) | — |
| G6 | Tray icon, glance tooltip, menu | Built | — |
| — | Toasts | Partial | tray balloons (the D-G7 fallback); real toasts with buttons need an app identity (MSIX) |

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
| H8 | **MSIX** + portable zip | Partial | the zip is built; **no MSIX** (manifest, packaging, start at logon, CLI alias). Signing is the owner's decision |
| H9 | Public repo docs | Built | README antivirus section to check |
| H11 | Plugin contract + authoring tools | Built | — |
| H12 | Releases, SHA256SUMS | Built | — |
| H13 | Update check | Later (spec) | — |
