# Tasktor — U3 Windows (feature spec)

**Status:** **signed**. D-W1–D-W10 decided (D-W7: public API only); OQ-W1 Alt+`; OQ-W2 one-time consent per rule. Open items are probes only (W1–W3, OQ-W3).
**Tier:** the whole unit is a **Module group, off by default, later phase** (00-frame.md C3.5).
**Reference low end:** i7-1355U, 12 GB RAM, Iris Xe, one monitor, Windows 11 26200.

Conventions: **[verified: source]** = checked against Microsoft docs; **[unverified]** = a claim to be proven in the named spike; **[community]** = third-party source, not Microsoft.

## What U3 starts with (signed elsewhere; cite, don't redesign)
- **C's `WinEventSource`** (one hook thread; `FOREGROUND` + `MINIMIZESTART/END` global, `NAMECHANGE` scoped to the foreground PID). Since U2 its hooks are **lease-driven**: I11 holds a lease without F on (C-S2). C1.4: window open / close is recorded **only when F runs**.
- **C's `CurrentWorkflow`** and the snapshot `state.workflow` block (C-H6), which I5 builds on.
- **B2.5:** "is the job's window in front" (one `GetForegroundWindow` check). **B's freeze rule:** never act on the foreground app's job, and a frozen job's windows are in `FrozenRegistry`.
- **U1:**
  - `ActionRouter` (catalog → origin policy → `GuardChain` → confirm token → off-loop handler → log); rules may call only reversible actions; `run` only from a clicked toast button (D-H4, D-H6).
  - D-H9: "the agent, CLI and viewers carry no hooks"; one small binary per hook.
  - Rule state `fullscreen` already exists in the grammar; U1's U3 ask: "F's window events feed rules' `event` and `state: fullscreen`".
  - D-H12: releases are unsigned, so **no UIAccess** (it needs an Authenticode-signed exe).
- **U2:** I13 (event mode) and E11 (focus timer) "optionally restore a saved layout (U3's I5; a no-op until U3 ships)". `window.missing` (I11) is already a rule event.

## Cross-cutting decisions

### Verified platform facts (shared by F1–F7)
- **DWM thumbnails.** `DwmRegisterThumbnail(dest, src)`: both must be top-level windows; the destination must be owned by the calling process (or be the desktop). Handle is per-process. Vista+. [verified: learn.microsoft.com dwmapi DwmRegisterThumbnail]. Any Tasktor window can therefore show live thumbnails of any other app's top-level window; it cannot draw into the taskbar's own flyout.
- **Cloaked windows.** `DWMWA_CLOAKED` reports why a window is hidden: by its app, by the Shell (e.g. on another virtual desktop), or inherited. Win8+. [verified: DWMWINDOWATTRIBUTE]. This is the documented way to filter ghost windows out of any switcher.
- **Frame bounds.** `DWMWA_EXTENDED_FRAME_BOUNDS` gives the visible frame in screen space (not `GetWindowRect`, which includes the invisible resize border). [verified: DWMWINDOWATTRIBUTE]
- **Low-level keyboard hook.** `WH_KEYBOARD_LL` is global-only; runs on the installing thread, which needs a message loop; the callback must return within `LowLevelHooksTimeout` (capped at 1000 ms since Win10 1709) or Windows **silently removes the hook** with no notification. Microsoft advises a dedicated hook thread that hands work off and returns at once. [verified: learn.microsoft.com LowLevelKeyboardProc]
- **UIAccess.** Lets a process reach higher-IL UI and be topmost. Requires: Authenticode-signed exe, installed in a UAC-protected location (e.g. Program Files), `uiAccess="true"` in the manifest. Launched by a non-admin user it runs at medium+ IL and **still cannot touch elevated ("high") windows**; only an admin user gets high IL. Microsoft states UIAccess **should not be used by apps that are not assistive technologies**. UWP/packaged apps cannot have it. [verified: learn.microsoft.com uiauto-securityoverview, updated 2026-02-18]
- **Virtual desktops, public API.** `IVirtualDesktopManager` has exactly three methods: `GetWindowDesktopId`, `IsWindowOnCurrentVirtualDesktop`, `MoveWindowToDesktop`. No enumerate, switch, create or name. Microsoft's guidance: apps should not switch the user's desktop; only the user should. [verified: learn.microsoft.com IVirtualDesktopManager]. Whether `MoveWindowToDesktop` works on windows of *other* processes is not stated on the page → [unverified, F4 spike].
- **Undocumented desktop COM.** Switch/enumerate needs `IVirtualDesktopManagerInternal` etc. The maintained wrapper (Ciantic VirtualDesktopAccessor / Rust crate `winvd` 0.0.49) now requires Windows 11 24H2 build 26100.2605+ and hardcodes interface GUIDs [community: github.com/Ciantic/VirtualDesktopAccessor, docs.rs/winvd]. That it needs a new build floor is itself the fragility evidence.
- **Fullscreen state.** `SHQueryUserNotificationState` returns `QUNS_BUSY` (full-screen app or presentation settings), `QUNS_RUNNING_D3D_FULL_SCREEN` (exclusive D3D), `QUNS_PRESENTATION_MODE`, `QUNS_NOT_PRESENT` (locked / screensaver). [verified: learn.microsoft.com QUERY_USER_NOTIFICATION_STATE]

### Existing tools (don't rebuild what users have)
| Tool | Covers | License / cost | Verdict for Tasktor |
|---|---|---|---|
| PowerToys **FancyZones** | Custom zones per monitor (grid/canvas), Shift-drag, override Win+Arrow, multi-zone span, cycle windows in a zone, quick layout hotkeys, exclusions, a **CLI** (`set-layout`, `get-layouts`, `--monitor`) [verified: learn.microsoft.com powertoys/fancyzones] | MIT, Microsoft, free | Covers almost all of F3. Do not rebuild. |
| PowerToys **Workspaces** | Capture a set of apps + positions, launch with CLI args, "move existing windows", desktop shortcut [verified: learn.microsoft.com powertoys/workspaces] | MIT, free | Covers the launch/restore half of I5. Admin apps can't be repositioned (known issue). No virtual-desktop handling mentioned. |
| **komorebi** | Full tiling WM (BSP etc.) | Komorebi License 2.0 (PolyForm-Strict fork), **no commercial use** without a paid licence [community: github.com/LGUG2Z/komorebi] | Power users who want tiling already have it; Tasktor should not compete. |
| **AltSnap** | Alt+drag move/resize anywhere | GPL, free | Leave to it. |
| Alt-Tab Terminator | Alt+Tab replacement with thumbnails + search | free / $19.95 Pro, license unclear [community: sourceforge, MakeUseOf 2026-02] | Proof the F2 niche exists; not open source. |
| VirtualDesktopAccessor / winvd | Switch / enumerate desktops (undocumented COM) | MIT | The only realistic way to do F5; inherits its build fragility. |

### D-W1 Scope against PowerToys: build what nobody else does with Tasktor's data
PowerToys is free, MIT, from Microsoft, and already does zones and app-set launching. Tasktor's edge is what it knows: learned workflows (C), running jobs (B) and frozen state. So U3 keeps the parts that use that knowledge and leaves the rest to PowerToys.
| Row | Verdict | Why |
|---|---|---|
| **F1** taskbar-hover thumbnails | **cut as a taskbar feature.** Its useful part (every window of one app, with live thumbnails, minimized ones too) becomes the switcher's **app view** (F2) | Windows 11's own hover flyout already shows each window of the app. Replacing it means detecting hover on the taskbar through UI Automation and drawing over a shell surface Tasktor does not own, which breaks on taskbar updates |
| **F2** switcher | **build** (`WindowSwitcher`, D-W3, D-W4) | thumbnails + search + workflow grouping + job / frozen badges: no free tool has the last two |
| **F3** tiling, zones | **cut; integrate FancyZones.** A preset may carry a FancyZones layout per monitor, applied through FancyZones' CLI (`set-layout`) when PowerToys is installed | FancyZones covers zones, drag, Win+Arrow override and layout hotkeys [verified: learn.microsoft.com powertoys/fancyzones]. komorebi covers tiling for power users |
| **F4** virtual desktops | **build, public API only** (D-W7): a "on another desktop" badge in the switcher; "bring here" only if the spike shows it works for other apps' windows | `IVirtualDesktopManager` has three methods, no enumerate or switch |
| **F5** switch desktops | **cut as its own feature.** Choosing a window on another desktop in the switcher activates it, which may switch desktops by itself **[unverified, W2]** | the only way to switch on purpose is undocumented COM with a hardcoded-GUID build floor (26100.2605+) |
| **F6** multi-monitor, per-monitor DPI | **requirement, not a feature** (D-W8) | every window Tasktor draws or moves must handle it |
| **F7** hide over fullscreen | **build as a source** (`FullscreenSource`, D-W9) that feeds U1's existing `fullscreen` state | every surface, and B's / U2's rules, can use it |
| **I5** workflow presets | **build** (`PresetStore`, `PresetRestorer`, D-W5, D-W6) | Workspaces captures a hand-picked set; it cannot start from a learned workflow, skip frozen jobs or undo |
| **G4** overlay bar | **defer: later, experimental, off** (D-W10) | the most fragile surface in the catalog; the tray and widget already give a glance |

### D-W2 One window-UI process, `tasktor-shell.exe`
- **What it is:** a small Native AOT Win32 process that hosts the switcher, its thumbnails and (if ever built) the overlay bar, plus the opt-in Alt+Tab hook (D-W3).
- **Started by** the agent's `ProcessSupervisor` only while the Windows Module group is on; restarted by D-H9's policy. It is a **pipe client** like the flyout (hello / subscribe / pull / act, D-H3). It has no network code (same analyzer and import checks as G-capture-net).
- **Why not the agent:** D-H9 keeps the agent hook-free and UI-light. The switcher must appear within ~100 ms of a key press, which needs a warm process with its window already created; the flyout exits 30 s after it hides, so it cannot be that process.
- **Why not `tasktor-capture`:** capture is T1 (records typing). An Alt+Tab hook that records nothing must not share a binary, a consent or an antivirus verdict with a keystroke recorder.
- **Split of work:**
  - the shell **draws and activates** (it received the key press, so it may set the foreground window **[verified: SetForegroundWindow doc, conditions]**);
  - the agent **decides and moves**: presets, matching, placement and undo run as `ActionRouter` handlers off the logic thread (D-A5 §7).
- **Budget:** idle ≤ 20 MB, zero wake-ups while hidden (blocked in `GetMessage`) **[estimate, W1]**.

### D-W3 Switcher trigger: own hotkey by default, Alt+Tab override opt-in
- **Default:** a configurable hotkey through `RegisterHotKey` (default **Alt+`**). No hook. While the switcher is open it has keyboard focus, so releasing Alt arrives as a normal key-up and commits the choice.
- **Opt-in "Replace Alt+Tab":** a `WH_KEYBOARD_LL` hook on a dedicated thread in `tasktor-shell` (the pattern Microsoft advises, as in D-C19).
  - The callback only compares the key against Alt / Tab / Shift / Esc and posts a message; it **stores and forwards nothing**. A CI test (G-redact family) checks the shell's hook module has no write path.
  - **Fallback to the native switcher**, by passing the key through, whenever: the shell is not running; an exclusive-fullscreen app is in front (D-W9); the hook may have been removed by the OS (the timeout removes it silently).
  - **Silent-removal guard:** reinstall the hook on resume, on unlock, and when `GetLastInputInfo` advanced through ≥ 50 key-like inputs with zero callbacks **[method unverified, W1]**.
  - **Elevated windows:** a medium-integrity hook is not expected to see keys while an elevated window has focus **[unverified, W1]**. Then the native Alt+Tab runs, which is the right fallback. Without UIAccess (unsigned, D-H12) Tasktor cannot do better.
- **Why opt-in:** an unsigned binary with a global keyboard hook is what antivirus heuristics flag (D-H12's antivirus section). Off by default keeps the default install free of hooks outside T1.

### D-W4 What the switcher lists and how
- **Which windows:** visible, top-level, not cloaked (`DWMWA_CLOAKED`; cloaked-by-Shell = another virtual desktop, shown with a badge, D-W7), not a tool window, owner chain resolved the way the shell's Alt+Tab does **[community: Raymond Chen, "Which windows appear in the Alt+Tab list?"; W1 compares against the native list]**.
- **Order:** most recently used, from `WinEventSource` foreground events. The Windows Module holds a **lease** on `FOREGROUND` + `MINIMIZE*` + (new for F) `OBJECT_SHOW` / `OBJECT_DESTROY` while on (C1.4: open / close only while F runs).
- **Views:** all windows (default); **app view** (press → on an app: all its windows, F1's useful part); **workflow view** when C's model is active (groups by C's per-window workflow; hidden otherwise).
- **Badges** from the snapshot: B's job row ("training · 52 min"), **frozen** (choosing it offers Resume first, never activates a suspended UI thread silently), on another desktop, admin (elevated; can't be moved, D-W6).
- **Search:** typing filters by title and app name. Titles stay **in the shell's memory only**; nothing about the switcher is stored except the MRU order of window handles, which is in memory too.
- **Thumbnails:** `DwmRegisterThumbnail` into the switcher window; the compositor draws them, so cost is per visible tile, not per frame of the source app [verified: dwmapi]. At most 24 tiles; more scrolls.

### D-W5 Presets (I5): what a preset is, where it comes from, what may apply it
**A preset** = a name + a list of **slots** + optional FancyZones layouts per monitor + an optional hotkey.
| Slot field | Stored | Notes |
|---|---|---|
| `app` | A's app id + exe path | A's `AppIdentity` (same ids as C) |
| `match` | window class + nth-of-app; an optional title pattern **the user types** | titles are never captured into a preset automatically (rule 5b) |
| `place` | monitor id, rect relative to the work area in DIPs, show state (normal / max / min) | D-W8 |
| `launch` | off by default; when ticked, exe + arguments **the user confirmed** on a review screen | arguments can hold paths; shown before saving |
| `desktop` | "current" only (D-W7) | |

**Where presets come from:**
1. **Save what is open:** a checklist of the switcher's windows, all ticked; Save.
2. **From a workflow** (needs C's model active): "Save preset for *Parking CV*" lists that workflow's top apps from C's window × workflow dwell matrix and the windows of those apps open now. The user edits the list and saves. Nothing is saved automatically.
3. **Suggested** (later, with C's next-workflow prediction, which ships only if it beats the Markov baseline): a report-only card line "Usually you open *Parking CV* now · Restore". Never applied by itself.

**What may apply a preset** (one action, two halves):
| Caller | Move existing windows | Launch missing apps |
|---|---|---|
| Click / hotkey / `tasktor preset apply <name>` / E11 focus Start | yes | yes, if the slot's `launch` is ticked |
| Rules (I13 event mode, any user rule) | yes (`arrange`, reversible, with `undo`) | **no** (`launch` is not reversible; U1 D-H4 keeps rules to reversible actions) |

- New actions: `arrange { preset }` (reversible; `undo` restores the previous placements, kept 30 min) and `launch_preset { preset }` (user-initiated only, like `run`).
- **PowerToys Workspaces** stays the user's tool for app sets without learning. Tasktor neither reads nor writes Workspaces files (their format is internal) **[unverified: whether Workspaces offers a stable CLI to launch a workspace by id; OQ-W3]**.

### D-W6 Placement never blocks, never fights, never touches what it must not
- **Never block:** every move uses `SetWindowPos` / `SetWindowPlacement` with `SWP_ASYNCWINDOWPOS`, so a hung or suspended window cannot stall the handler. Windows of **frozen jobs** (`FrozenRegistry`) and windows where `IsHungAppWindow` is true are **skipped**, and the result says so (B's rule: never act on frozen-job windows).
- **Elevated windows** cannot be moved from a medium-integrity process (UIPI); PowerToys Workspaces lists the same limit as a known issue [verified: learn.microsoft.com powertoys/workspaces]. Skipped with "admin window" in the result.
- **Frame correction:** target rects are visible-frame rects; the placer subtracts the invisible resize border measured with `DWMWA_EXTENDED_FRAME_BOUNDS` vs `GetWindowRect` per window.
- **Don't fight the user:** a restore takes ≤ 20 s (launched apps' windows are matched by `OBJECT_SHOW` events). If the user moves or closes a window during it, that slot is dropped. Restore never steals focus except for the slot marked `focus`.
- **Matching** is greedy per app in MRU order; each window is claimed once; unmatched slots launch (if allowed) or are reported "not open".
- **Result:** a per-slot list (moved / launched / skipped: reason) shown as a toast with **Undo** (D-H6's ≤ 3 buttons).

### D-W7 Virtual desktops: public API only
- `IsWindowOnCurrentVirtualDesktop` + cloaked-by-Shell → the switcher's "on another desktop" badge. The switcher lists all desktops' windows (setting: "this desktop only", like Windows' own option).
- **"Bring here"** (`MoveWindowToDesktop` with the current desktop's id, read from the shell's own window) ships only if spike **W2** shows it works on other processes' windows; community reports say it returns access denied for windows the caller does not own **[community; unverified, W2]**.
- **No switching, enumerating or naming desktops.** Undocumented COM stays out of the public build (F5 cut). A third-party plugin could add it; plugins cannot inject into the shell, so that would be a separate tool.

### D-W8 Monitors and DPI (F6 as a requirement)
- Every Tasktor window process is **Per-Monitor-V2 DPI aware** (manifest).
- Presets store the **monitor's device path** (stable across reboots) plus the rect in DIPs relative to that monitor's work area. On restore: same monitor → scale to its current DPI; monitor missing → the primary, rect scaled to fit; never off-screen.
- The switcher opens on the monitor with the cursor.

### D-W9 Fullscreen (F7): one source, one state
- **`FullscreenSource`**: on each foreground change (from the `WinEventSource` lease, no polling) it reads `SHQueryUserNotificationState` [verified] and checks whether the foreground window's frame covers its whole monitor. It sets `state.fullscreen` = `none` | `borderless` | `exclusive` | `presentation`.
- **Who uses it:**
  - U1's rule grammar (`state: fullscreen`), for example U2's guards and "don't show toasts during a game" rules;
  - the shell: the switcher's Alt+Tab override passes through in `exclusive`; the overlay bar (if ever) hides in any fullscreen;
  - B and C may read it (report only; no new behaviour here).
- The Windows Module's lease is not needed for this source alone: it holds its own lease on `FOREGROUND` only, so the `fullscreen` state works with the rest of U3 off.

### D-W10 G4 overlay bar: defer, and what it would take
- Windows 11 removed toolbar deskbands, so there is no supported way to put UI *in* the taskbar **[community; verify, W3]**. Tools that do it draw a **topmost window over the taskbar's empty area** and fight for z-order each time the taskbar is clicked.
- Needs: the taskbar's free region from UI Automation (icons can be centered or left, widgets on or off, tray width changes), repositioning on every change, hiding in fullscreen (D-W9), and re-asserting topmost on foreground changes.
- **Proposal:** not in v1. Row stays as "later, experimental, off". If built, it lives in `tasktor-shell`, renders A's blocks through U4's `IBlockRenderer` (one line: CPU, RAM, the top job's progress) and is the first thing to switch off when a Windows update breaks it.

## Features (U3)
| Row | Feature | Kind | Built from | Tier |
|---|---|---|---|---|
| F1 | (cut as a taskbar feature) → switcher app view | — | `WindowSwitcher` | — |
| F2 | Window switcher: own hotkey, opt-in Alt+Tab override, thumbnails, search, app / workflow views, job / frozen / desktop / admin badges | Module | `tasktor-shell` + `WindowList` + `DwmThumbnails` + snapshot | T2 |
| F3 | (cut) zones → FancyZones; a preset may set a FancyZones layout via its CLI | — | `FancyZonesBridge` | T2 |
| F4 | "On another desktop" badge; "bring here" if W2 passes | part of F2 | `VirtualDesktopInfo` | T2 |
| F5 | (cut) choose a window on another desktop → activation may switch | — | — | — |
| F6 | Per-monitor DPI + monitor identity for all window code | requirement | `MonitorMap` | — |
| F7 | `fullscreen` state for rules and surfaces | Core source | `FullscreenSource` | T1 |
| I5 | Workflow presets: save (open windows or from a workflow), apply by click / hotkey / CLI / focus start; rules may only `arrange` | Module | `PresetStore` + `PresetRestorer` + `WindowPlacer` | T2 (learned suggestion L2) |
| G4 | Overlay bar | deferred | — | — |

## Architecture (classes on A's types)
| Class | Assembly / process | Kind | Responsibility | Row |
|---|---|---|---|---|
| `FullscreenSource` | Windows / agent | `IEventSource<FullscreenChanged>` | foreground-change driven; feeds `state.fullscreen` | F7 |
| `WindowList` | Windows / agent | helper | enumeration + Alt+Tab-equivalent filter + MRU from `WindowEvent`s; snapshot table `windows` (handles, app ids, flags; **titles not in the snapshot file**, only over the pipe to the shell) | F2 |
| `VirtualDesktopInfo` | Windows | helper | `IVirtualDesktopManager` wrapper; `IsOnCurrent`, `TryBringHere` | F4 |
| `MonitorMap` | Windows | helper | device paths, work areas, DPI; DIP ↔ px | F6 |
| `WindowPlacer` | Windows / agent | action handler (off-loop) | async moves, frame correction, skip rules, before-state for `undo` | I5 |
| `PresetStore` | Core | store | `presets.json` (schema `tasktor.presets/1`), user-confirmed fields only | I5 |
| `PresetBuilder` | Core | helper | from open windows, or from a workflow via C's dwell matrix | I5 |
| `PresetRestorer` | Core | `ICardProvider` + action handlers `arrange`, `launch_preset` | matching, launch (user-initiated only), 20 s window, result + Undo toast | I5 |
| `FancyZonesBridge` | Windows | helper | detects PowerToys; runs the FancyZones CLI `set-layout` (5 s timeout) | F3 |
| `ShellHost` | `tasktor-shell` | process main | pipe client, hotkey, hook thread (opt-in), switcher window | F2 |
| `KeyChordHook` | `tasktor-shell` | hook thread | Alt+Tab detection only; posts to the UI thread; stores nothing | F2 |
| `WindowSwitcher` | `tasktor-shell` | UI | tiles, search, views, badges, activation; Resume-first on frozen | F2 |
| `DwmThumbnails` | `tasktor-shell` | helper | register / update / unregister per visible tile | F2 |

**Interfaces:** no new interface. `FullscreenSource` is an `IEventSource`; `PresetRestorer` is an `ICardProvider` with catalog actions; the shell is a pipe client. `IProcessControl` is unchanged (launching a preset app is a user-initiated shell launch, not a worker).

## Time and space
| Path | Cost |
|---|---|
| Foreground event → MRU + fullscreen check | O(1) + one `SHQueryUserNotificationState` + one rect compare |
| Open switcher | enumerate ~50 windows (`EnumWindows` + 2 DWM attribute reads each) ≈ 1–2 ms **[estimate, W1]**; register ≤ 24 thumbnails |
| Search keystroke | O(n) filter over ≤ ~200 windows |
| Apply preset | O(slots × windows of that app) matching; moves are async |
| Memory | agent + ≤ 0.5 MB (MRU, presets); shell ≤ 20 MB idle **[estimate]** |
| Disk | `presets.json`, a few KB |

## Open questions and spikes
| # | Question | Spike / owner |
|---|---|---|
| W1 | Shell idle memory; enumeration time; does the LL hook see keys while an elevated window has focus; silent-removal guard works; our list matches native Alt+Tab | W1 probe (Windows 11 26200) |
| W2 | `MoveWindowToDesktop` on another process's window; does activating a window on another desktop switch to it | W2 probe |
| W3 | Is there any supported way to put UI inside the Windows 11 taskbar (only matters if G4 is ever built) | W3 doc check |
| OQ-W1 | Default switcher hotkey | **decided: Alt+`** | — |
| OQ-W2 | Consent for rules that call `arrange` | **decided: yes, once per rule**, asked on the first save of a rule that calls `arrange` | — |
| OQ-W3 | Workspaces CLI: if a stable "launch workspace by id" exists, a preset could call it | doc check |

## Changes for the catalog (for signing)
- F1 → cut as a taskbar feature, folded into F2's app view.
- F3 → cut; FancyZones layout per preset via its CLI.
- F5 → cut; activation from the switcher.
- F6 → requirement on all window code.
- F7 → `FullscreenSource` (Core source, T1).
- G4 → later, experimental, off.
- I5 → presets as above.

**Asks for other units:** (consolidated below)
- **U1 (H):**
  - D-H9 process table gains `tasktor-shell.exe` (Windows Module only; ≤ 20 MB idle);
  - "the agent, CLI and viewers carry no hooks" stays true; **the hook list becomes: `tasktor-capture` (T1) and `tasktor-shell` (opt-in Alt+Tab only, stores nothing)**;
  - actions `arrange` (reversible, `undo`) and `launch_preset` (user-initiated only);
  - public contract `tasktor.presets/1`; store file `presets.json`, treated as user content like `notes\` (kept by `forget tracking` and `forget all`; included in settings export);
  - `state.fullscreen` values `none | borderless | exclusive | presentation`.
- **C:** `WinEventSource` gains `OBJECT_SHOW` / `OBJECT_DESTROY` under the Windows Module's lease (C1.4 already allows it while F runs); `PresetBuilder` reads the window × workflow dwell matrix (read-only).
- **B:** the switcher reads `FrozenRegistry` and job rows; choosing a frozen window offers Resume first.
- **U2:** I13 and E11 call `arrange` (rule) / apply (focus Start) once U3 ships.
- **U4:** the Presets card, the switcher's look (shared tokens with the flyout), the preset review screen, and the Settings page for the Windows Module.

## Consolidated at signing (2026-11-04)
U3 is signed. This file does **not** edit other sections. Each row below is a change for its owner. "Must" means U3 depends on it.

**U3 decisions in one place**
| Id | Decision |
|---|---|
| D-W1 | Build F2 switcher, I5 presets, F7 fullscreen state, F4 badge; cut F1 (→ switcher app view), F3 (→ FancyZones CLI per preset), F5 (→ activation); G4 later, experimental, off |
| D-W2 | All window UI in a new `tasktor-shell.exe` (Windows Module only, pipe client, no network); the agent decides and moves, the shell draws and activates |
| D-W3 | Default trigger Alt+` via `RegisterHotKey` (no hook); Alt+Tab override opt-in, hook in the shell only, stores nothing, falls back to the native switcher |
| D-W4 | Alt+Tab-equivalent window list, MRU, all / app / workflow views, job / frozen / desktop / admin badges, ≤ 24 thumbnails, titles in memory only |
| D-W5 | Presets: slots (app, match, place, optional confirmed launch); from open windows or a workflow; click / hotkey / CLI / focus Start may move + launch; rules may only `arrange` (reversible, `undo`), after a one-time consent per rule |
| D-W6 | Placement is async, skips frozen / hung / admin windows, yields to the user, never steals focus, result toast with Undo (30 min) |
| D-W7 | Virtual desktops through the documented interface only; badge; "bring here" only if W2 passes; no switching / listing / naming |
| D-W8 | Per-Monitor-V2 DPI everywhere; monitors by device path; rects in DIPs relative to the work area |
| D-W9 | `FullscreenSource` sets `state.fullscreen` (`none` / `borderless` / `exclusive` / `presentation`), with its own foreground lease |
| D-W10 | G4 deferred; if built, lives in the shell and renders A's blocks |

**U1 (H, signed): changes needed**
| # | Change | Kind |
|---|---|---|
| H-W1 | D-H9 process table gains `tasktor-shell.exe` (started by `ProcessSupervisor` only while the Windows Module is on; ≤ 20 MB idle; pipe client; restart policy as capture) | must |
| H-W2 | "The agent, CLI and viewers carry no hooks" stays; hook binaries become `tasktor-capture` (T1) and `tasktor-shell` (opt-in Alt+Tab only, stores nothing). G-capture-net's no-network checks also apply to `tasktor-shell`; a G-redact test checks the shell hook has no write path | must |
| H-W3 | New actions `arrange { preset }` (reversible, `undo`; rules allowed after a one-time consent per rule) and `launch_preset { preset }` (user-initiated only, like `run`) | must |
| H-W4 | New public contract `tasktor.presets/1`; store file `presets.json`, user content like `notes\` (kept by both forget commands, included in settings export) | must |
| H-W5 | Rule state `fullscreen` gets the values `none` / `borderless` / `exclusive` / `presentation` | must |
| H-W6 | D-H10 budget row: `tasktor-shell` ≤ 20 MB idle, zero wake-ups while hidden; agent + ≤ 0.5 MB with the Windows Module on | must |

**A:** none. *(A's slot map already reserves window events for F.)*
**B:** the switcher reads job rows and `FrozenRegistry` (read-only); choosing a frozen window offers Resume first; `WindowPlacer` skips frozen-job windows. *(must; no change to B's own behaviour)*
**C:** `WinEventSource` adds `OBJECT_SHOW` / `OBJECT_DESTROY` under the Windows Module's lease (C1.4 already allows it while F runs); `FullscreenSource` holds its own `FOREGROUND` lease; `PresetBuilder` reads the window × workflow dwell matrix (read-only); the switcher's workflow view reads C's per-window workflow. *(must)*
**U2:** I13 (event mode) calls `arrange`; E11 focus Start applies a preset (move + launch). *(can, once U3 ships)*
**U4 (surfaces):** the Presets card; the preset review screen (launch arguments shown before saving); the switcher's look (shared tokens with the flyout); Settings → Windows (hotkey, Alt+Tab override, "this desktop only"); the rule-consent dialog for `arrange`. *(must)*
