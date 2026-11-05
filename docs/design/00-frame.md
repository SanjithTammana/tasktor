# Project: Tasktor

> Compiled 2026-11-03 from one planning session: Muse's Docktor-port prompt, audited and rescoped,
> plus the user's use cases, the user's existing projects, and the event-tracking + clustering track.

**Status:** frame
**Blocked by:** decision (sign-off)
**Last touched:** 2026-11-03
**Stack (justified; decided 2026-11-04 in section A, OQ12):** **C# everywhere**. The always-on agent `tasktor-core` is published as **Native AOT** (spike on the reference PC: 6.1 MB private, 0.002% CPU idle, no runtime to install); the Widgets-board provider and the flyout are C# / .NET 8 + Windows App SDK. Every UI is a thin viewer over the agent's state. Rust is out; agent-desktop's Rust crates are no longer a direct dependency (UI Automation comes through source-generated COM). Four runtime assemblies: Contracts / Core / Windows / Hosts, plus test projects holding all fakes. **Learning split:** online statistics (B's per-kind learned values, online cluster assignment) live in C# Core; model fits run in the Python worker. **Learning is Python, and a required part of the app**. Users still never install Python themselves: the release bundles its own Python runtime and the learning worker (python.org's **embeddable Python 3.13** package, decided in section C, D-C11; WS9 sizes it). The worker runs in a kill-on-close Job Object and is never resident, so a download from GitHub works as is. Developers can run the same worker from a normal venv. Seam = one versioned JSON snapshot (file + named pipe), also printed by `tasktor status --json`. Extensions are out-of-process plugins in any language (C4), so contributors do not need Rust. Fallback if the seam hurts: all C# for agent + widget.
**Environment:** C#: `global.json` + NuGet lockfile; Native AOT publish needs VS "Desktop development with C++" and the VS Installer folder on PATH (vswhere). Python: pinned version + lockfile (uv or pip-tools). `bin/ obj/ .vs/ .venv/` gitignored. No machine-specific path, name or setting anywhere in the repo.

## Product scope (reoriented 2026-11-03)
Tasktor is a **public, downloadable Windows 11 app** that anyone can install, configure and extend. the maintainer is its first user, not its only one.
- **Generic by default.** Nothing in the repo assumes any private tooling, folders or contacts. Paths come from Windows Known Folders and settings; examples in docs are generic.
- **Three tiers** (`01-catalog.md`): **Core** (on, no setup), **Module** (ships, off until the user turns it on), **Pack** (plugins outside the repo).
- **Changeable without forking.** Users change behaviour through settings, an editable rules file (H10) and plugins (C4). Forking the open-source code is the last resort, not the first.

## Engineering principles
- **Data-first (D-A5).** Design from the data. Four shapes (series, table, tree, event) each get one set of reusable classes; interfaces only at real seams (seven in the agent, plus `IBlockRenderer` in viewers). Built-in features and plugins share `ICardProvider`.
- **Shared core, not section-owned.** Contracts and Core are project infrastructure (WS0/WS1/WS2). Every section reuses shape classes first; a new shape, interface or block kind is a project-level change reviewed against all sections.
- **OO discipline (D-A4).** Encapsulated objects, composition over inheritance, OS access only through fakeable platform adapters, push events over polling, minimum time and space with no loss of accuracy, a complexity table per hot path.
- **One logic thread (D-B11).** Sources only enqueue; actions run off the loop.
- **Adaptive with fallbacks (D-B9).** A learned per-key value with the fixed rule as fallback and guardrails; trust ramps with evidence.
- **Sampling exceptions to "only while viewed":** system totals every 60 s, last hour in memory (D-A1); job watch's names-only process scan on the same tick (D-B2).

## Problem
One local app that (1) keeps a glanceable, actionable view of the machine inside the Windows shell, (2) optionally and passively records how the user works, and (3) learns recurring workflows by unsupervised clustering and runs those models persistently, so the live view can say what workflow the user is in. "Working" = install from a release, open the Widgets board (or the tray flyout in the portable build) and see live load, the top processes and the current workflow cluster, and end a runaway process in two clicks.

## Lifecycle (replaces kill conditions)
No calendar deadlines.
- **Data keeps accruing while dormant.** The agent runs on its own, so time away is collection time. Collection stops only on `tasktor pause` or uninstall.
- **Quality gates, not time gates.** A track advances when its evidence passes (below); it never expires.
- **Self-measured use.** The agent logs its own card opens, so any later review uses data, not memory.

## Platform facts (verified 2026-11-03, sources)
- Windows 11 has no supported way to add UI into the taskbar: deskbands, toolbars and app customisation of the taskbar were removed; bottom-only position (Wikipedia: List of features removed in Windows 11; no Microsoft statement found on exact builds).
- **Widgets board:** third-party widgets via a packaged (MSIX) widget provider, Adaptive Cards, worldwide; local testing needs Developer Mode. HTML "web widgets" are EEA-only. The provider runs only while the widget host activates it (learn.microsoft.com/windows/apps/develop/widgets/*).
- **Dashboards** (a tab next to Discover) are feed providers: packaged app, HTML in an iframe, WinAppSDK 2.3.1+, preview, "available only to users in the EEA"; how the gate is evaluated is undocumented (learn.microsoft.com/windows/apps/develop/feeds/*).
- **agent-desktop** (vercel-labs, Apache-2.0, pinned v1.0.2 / commit a47ed664): Rust; `agent-desktop-core` (OS only through a `PlatformAdapter` trait), `agent-desktop-windows` (UI Automation), CLI, FFI. No event/watch API. Only the README has been read. Since the C# decision it is reference only (patterns, not a dependency).
- **Docktor** (inspiration only): v0.15.2 beta, macOS 26+, free, closed source (releases repo holds appcasts only). Clean-room; no code, names or assets reused.

## Components
### C1. `tasktor-core` agent (C# Native AOT, always on)
| Sub-component | Job |
|---|---|
| C1.1 Samplers | CPU, RAM, GPU engine, disk free + activity, network, battery, TCP listeners, per-process rows grouped by app. Run only while a viewer is open or an alert/job rule needs them. |
| C1.2 Event capture | **T0 (section C, D-C8):** foreground change (title hook scoped to the foreground process), idle start/end, process start/stop from B's 60 s names-only scan, optional browser domain via cached UIA. C# `WinEventSource` / `IdleSource`. Window open/close belongs to F's source and is recorded only when F runs. Notifications are off (MSIX only). No keyboard or mouse hooks in T0. **T1 deep capture (D-C16–D-C22)** runs in a separate `tasktor-capture` process: actor attribution, key runs, UIA snapshots + line diffs, a companion browser extension, pointer, media, and a CLI log. agent-desktop is a reference and a dev-time probe only (D-C14). Passive: no click, type or launch path. |
| C1.3 Job watch | python/node/ffmpeg rows: age, CPU time, priority, command line; silent-job flag; opt-in `jobs/<id>.json` progress files. |
| C1.4 Alerts | Toasts for disk below threshold, silent job, RAM pressure. |
| C1.5 Plugin host | Discovers plugins (C4), runs them on their schedule with a timeout and low priority, validates their output against the card schema, merges it into the snapshot, routes card button clicks back to the plugin. A broken plugin shows an error card; it never takes the agent down. |
| C1.6 Model runtime | In C# (D-C10), event-driven: loads the model as memory-mapped sidecar files, assigns each new activity online, writes cluster id + confidence to the snapshot. Confidence floor 0.5 and 2-result hysteresis; under the floor it falls back to activity cluster, then app. |
| C1.7 Store | Signed in U1 (D-H8): root `%LOCALAPPDATA%\Tasktor\` (Known Folders; MSIX `ExcludedDirectories` fix; portable `--data-dir`). Layout: `config.json`, `rules.json`, `snapshot.json`; `state\` (DPAPI install key, consents, pause, B's learned / frozen state, self-use counts); `jobs\`, `agents\`; `track\` (T0 `events.db` 30 days, `episodes.db`, training extract, models; `winsqlite3`, one writer thread, D-C9); `capture\` (T1 AES-GCM vault 7 days + SQLCipher index); `plugins\`, `plugin-data\`, `log\`. Data never enters a repo. |
| C1.8 Control + IPC | Signed in U1 (D-H3, D-H4): `snapshot.json` (state + cards, atomic, ≤ 1/s on change) and a current-user named pipe (`hello` / `subscribe` / `pull` / `act` / `lease`), never TCP. Every action from any origin goes through one `ActionRouter` (catalog → origin policy → GuardChain → confirm token → off-loop handler → log). CLI `tasktor`: `status`, `job`, `proc`, `pause` / `resume`, `summary`, `forget`, `settings`, `log`; destructive CLI actions need `--yes` + `cli.allow_destructive` (off). |

### C2. Learning pipeline (Python worker, bundled, launched by the agent, runs offline at low priority)
| Sub-component | Job |
|---|---|
| C2.1 Episode builder | Raw events → episodes (contiguous app/title runs split by idle gaps). Deterministic; built online in the C# agent so live assignment works, read by the Python worker for training, exportable (C14). One spec, tested against shared fixtures on both sides. |
| C2.2 Features | Computed in C#. Per activity: app id, keyed-hash title tokens (df ≥ 3 → LSA 64 → PCA 16 density step), time of day, weekday, duration, idle ratio, previous/next app, a job-was-running flag from B. Resource profile cut. Per workflow window: bag of apps and switches. T1 adds activity documents + embeddings (D-C20, gate G8). |
| C2.3 Clustering | Baseline k-means; main HDBSCAN (unknown cluster count, marks noise). Sequence view: recurring app-transition motifs (n-gram / PrefixSpan) for "workflows" that span apps. |
| C2.4 Evaluation (no labels) | Internal: silhouette / DBCV. Stability: bootstrap and day-split agreement (ARI), cluster persistence across refits. Thresholds pre-registered before the first fit. |
| C2.5 Naming | Each cluster gets a deterministic name from its top apps and title terms; the user can rename or merge in one command. Naming a handful of clusters replaces labelling every episode. |
| C2.6 Refit + export | Nightly or when idle and plugged in, at BelowNormal with capped threads, streamed progress and checkpoints, shown in the job-watch card. Cluster ids kept stable across refits by member-overlap (Hungarian) matching, so names survive (D-C12). Adaptive cadence 7 → 28 days plus a drift-triggered early refit; rollback on a failed gate. Export plain JSON (centroids, vocab, thresholds). |

### C3. Surfaces (viewers over the snapshot)
| Id | Surface | Status |
|---|---|---|
| C3.1 | Widgets-board widget (C# shim; small/medium/large Adaptive Cards; Action.Execute buttons) | first viewer |
| C3.2 | Own flyout window (hotkey / tray / pinned button; live charts) | after widget |
| C3.3 | Pinned taskbar button (badge, progress, custom hover card, jump list via ITaskbarList3 + DWM iconic thumbnails) | later; Win11 behaviour to verify |
| C3.4 | Dashboard tab next to Discover (feed provider) | spike only (EEA gate) |
| C3.5 | Overlay bar in taskbar free space + window previews + Alt+Tab switcher + tiling | separate Frame, later |

### C4. Plugin contract (public) and packs
Signed in U1 (D-H7); the full contract is in `features/U1-platform.md`. "Script prints, app renders", with a schema instead of free text.
- **Install:** a folder in `%LOCALAPPDATA%\Tasktor\plugins\<id>\` with `plugin.json` (`tasktor.plugin/1`): id, name, version, command, interval (≥ 30 s or manual), timeout (≤ 60 s), plain-language `reads` / `network`, settings, actions. Off until enabled; consent shows the exact command line and pins the hashes, so any change asks again.
- **Output:** one JSON document, ≤ 64 KB: ≤ 3 cards built from a fixed subset of A's blocks (Metric, Sparkline, Bar, Gauge, Table, List, Progress, Badge, Countdown, Actions), with literal values. They render like built-in cards, with a source badge.
- **Actions:** only the plugin's own declared actions, which call it back; plugins never reach a process action.
- **Limits:** one process per run, BelowNormal, 256 MB Job Object, killed at timeout, off after 3 failures. Plugins never get the event store, the capture vault, settings or the install key; they may read `snapshot.json`. Tasktor cannot block a plugin's network, and the consent dialog says so.
- **Credentials stay in the plugin.** Tasktor holds no tokens for anything a plugin talks to.

### C5. Packaging, distribution and startup
Signed in U1 (D-H9, D-H12):
- **Processes:** `tasktor-core.exe` (agent + tray icon), `Tasktor.Flyout.exe`, `Tasktor.Widget.exe` (MSIX only), `tasktor.exe` (CLI), `tasktor-capture.exe` (T1), `tasktor-nmh.exe`, the embeddable Python worker, plugin runs.
- **MSIX build:** everything, with a startup task, `runFullTrust` and the excluded-directory fix; sideloaded until a Store listing.
- **Portable build:** zip without the widget; logon start via an opt-in `HKCU\...\Run` entry.
- **Unsigned releases**, deep capture included. The README explains SmartScreen and antivirus warnings, how to check `SHA256SUMS.txt`, and how to allow the capture binary. Signing routes (SignPath Foundation, Azure Artifact Signing, Store) stay recorded for later.
- **GitHub Releases** for both, built by CI from `v*` tags. **winget** later. License **Apache-2.0**.

## Cards (what the user sees)
Full feature catalog, including the windowing and glance features from the original Muse spec: `01-catalog.md`. This table is the card subset.
| Card | Data | Tier |
|---|---|---|
| U1 Triage by app | C1.1 | 1 |
| U2 Disk guard | C1.1 + folder scan on demand | 1 |
| U3 Long-job watch | C1.3 | 1 |
| X1 Current workflow + time per workflow | C1.6 + C2 | 1 (shows once clusters pass their gate) |
| U5 Deadlines | module (file / ICS) | 2 |
| U6 Dev ports | C1.1 | 2 |
| U7 Battery / unplugged-under-load | C1.1 | 3 |
| U9 Repo hygiene | git on demand | 3 |
| Skipped | weather, timer, focus (Windows ships them), music, phone battery (no API), clipboard | |

## Interaction story
Logon → `tasktor-core` starts → event capture writes raw events → episode builder closes an episode on app switch or idle → model runtime assigns it to a workflow cluster → snapshot updated. He opens the Widgets board → widget shim activates, reads the snapshot every 1-2 s → shows load, top apps, current workflow. He clicks End on a row → shim sends the action over the pipe → agent asks for confirmation → ends the process tree → snapshot refreshes. Overnight while plugged in → the agent launches the bundled Python worker at low priority to refit over the new episodes → stable-id export → agent hot-reloads the model.

## Privacy rules (binding once signed; published in the README as the product's promise)
1. Local only. Nothing leaves the PC; no telemetry. Network use happens only inside modules or plugins the user turned on, and each says what it contacts.
2. Tiered (section C, D-C22). **T0 tracking** never records keystrokes, screen contents, clipboard or field values; it has no keyboard or mouse hooks, and UI Automation reads only the browser hostname. **T1 deep capture** is a separate opt-in Module with its own consent screen and an always-visible indicator. It records only the signed-in user who turned it on and cannot run hidden. Password and secure fields are suppressed and redaction runs before any write. Raw content is kept 7 days in a per-record AES-GCM vault; the index is kept; the capture process has no network code; `tasktor forget --all` removes everything. **T2** (OCR fallback and anything beyond) is a further separate opt-in.
3. Workflow tracking is off until the user turns it on. Pause persists across restarts; excludes for apps and title regexes apply before anything is written, and ship with defaults (password managers, private/incognito windows).
4. Raw events kept 30 days by default; episodes, features, models and names kept; one command deletes everything.
5. Optional DPAPI encryption at rest.
5a. Learned per-kind statistics (B6) are numbers only, keyed by a hash, never names or command lines; one command forgets them.
5b. Workflow models keep only hashed token buckets, numbers, app ids and words that recur (≥ 5 activities on ≥ 3 days); rare words expire with raw events after 30 days; the learning worker never sees a window title, only a numbers-only training extract.
6. Plugins never get the event store, only the public snapshot. For the user's install: `%APPDATA%\Tasktor\events\` never enters a repo, memory, or a model prompt; private tooling sees only a bounded, read-only summary produced on request.

## Phases
| Phase | Output | Advances when |
|---|---|---|
| **A0** Agent skeleton | .NET solution (Contracts / Core / Windows / Hosts + tests), Native AOT agent, C1.1 process + disk samplers, `snapshot.json`, `tasktor status --json` | Frame signed |
| **A1** Tracker | C1.2 foreground + idle + process events, pause + excludes, C1.7 store; agent-desktop audit and depend / vendor / reimplement decision | privacy rules signed |
| **W0** Widget spike | hello widget in the Add widget picker; measure update rate and Action.Execute | Developer Mode + .NET 8 SDK approved |
| **W1** Tier 1 cards | U1, U2, U3 in the widget, reading the snapshot | W0 answers its unknowns |
| **L1** First clusters | C2.1-C2.5 on accumulated episodes; pre-registered stability thresholds | at least ~300 episodes recorded (a data threshold, not a date) |
| **L2** Persistent models | C2.6 refit + C1.6 online assignment + X1 card | L1 clusters pass stability |
| **P1** Plugin host + contract | C1.5, C4 schema, one example plugin in the repo, a private pack started | WS0 contracts signed |
| **T2** Tier 2 cards | U5, U6 as modules; U4 via a private pack | after W1 and P1 |
| **V1** First public release | MSIX + portable builds on GitHub Releases, tray + flyout, README with the privacy promise, LICENSE, plugin guide | W1 + P1 done, H5 budgets met on the user's PC |
| **Later** | Store listing, winget, C3.3 pinned button, C3.4 dashboard spike, Tier 3 modules, C3.5 overlay Frame | on the user's call |

A0 and A1 come first because clustering needs history: the sooner the tracker runs, the sooner L1 has data.

## Unknowns
| Unknown | Resolved by |
|---|---|
| Widget update rate; Action.Execute in a non-authenticated host context | W0 |
| agent-desktop crates usable as a dependency (API shape, Windows build, size) | A1 audit |
| Chrome exposes the address bar via UIA reliably (for browser domain) | A1 |
| Per-process GPU % on Iris Xe via PDH GPU Engine counters | W1 |
| TF-IDF titles separate the user's workflows, or an embedding is needed | L1 |
| Feed-provider tab appears on a US-region PC | C3.4 spike |
| Native AOT agent + widget provider in one MSIX with a startup task; COM server (widget provider) under Native AOT | W0/A1 |
| Bundled Python worker (embeddable 3.13, decided in C): download size, refit speed on a low-end laptop | L1 / WS9 |
| Store certification accepts an always-on agent + widget provider | V1 |
| Name "Tasktor" is free to use publicly (no conflicting product or mark) | before V1 |

## Machine changes needing the user's explicit OK (none done)

## Sign-off
- [ ] the maintainer agrees this is how the system works → CONTRACTS.md
- [ ] Privacy rules signed
- [x] Learning stays Python, bundled
- [x] Public-distribution decisions (U1, 2026-11-04): Apache-2.0, unsigned releases with T1 included, v1 surfaces = widget (MSIX) + tray / flyout. Repo visibility = public (default); the repo is still to be created, with the user's OK.
