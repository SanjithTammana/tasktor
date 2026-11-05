# Implementation plan

The design in `docs/design/` is signed. This file turns it into ordered milestones. Each milestone names its spec sections, what to build and what "done" means. Work through them in order; earlier milestones are foundations for later ones.

## Repo layout (create it in M0; keep to it)
```
Tasktor.sln
Directory.Build.props          nullable, warnings as errors, LangVersion latest, analyzers
global.json                    .NET SDK 10
src/
  Tasktor.Contracts/           net10.0   records for every public contract, block vocabulary, STJ source-gen context
  Tasktor.Core/                net10.0   shapes, scheduler, state, cards, actions, rules, store logic, plugin host logic, features
  Tasktor.Windows/             net10.0-windows (EnableWindowsTargeting)  Win32 / WinRT adapters: sources, IProcessControl, notifier, DPAPI
  Tasktor.Hosts.Common/        net10.0   BindingResolver, CardFitter, SnapshotClient, ActionInvoker, theme tokens
  Tasktor.Agent/               tasktor-core.exe   Native AOT host: logic loop, pipe server, tray, supervisor
  Tasktor.Cli/                 tasktor.exe        pipe client + TextRenderer
  Tasktor.Capture/             tasktor-capture.exe  T1 deep capture (no network code)
  Tasktor.Nmh/                 tasktor-nmh.exe    browser native-messaging host
  Tasktor.Shell/               tasktor-shell.exe  window switcher + presets UI (U3)
  Tasktor.Flyout/              WinUI 3 flyout (Windows-only build)
  Tasktor.Widget/              Widgets-board COM provider (Windows-only build, MSIX)
tools/Tasktor.SchemaGen/       generates schemas/*.json from the records
schemas/                       generated; committed; CI checks they're current
plugins-builtin/               tasktor.calendar, tasktor.repos (first-party plugins)
examples/plugins/              one example plugin
worker/                        Python learning worker + tasktor_data loader (pyproject, tests)
extension/                     browser extension (T1)
tests/                         one test project per src project (xUnit), fakes for every interface
.github/workflows/             ci.yml (Linux + Windows), release.yml
```
The shared-core rule (spec A, D-A5) holds: four data shapes (series, table, tree, event), the seven interfaces (`IMetricSource`, `ITableSource`, `IEventSource`, `IProcessControl`, `INotifier`, `ICardProvider`, `IClock`) plus `IBlockRenderer` in the viewer hosts. **Don't add an interface or a shape without a spec saying so.**

## Milestones
Legend: **[L]** fully testable on Linux. **[W]** needs a real Windows check (compile it, fake it in tests, and list the check in PROGRESS).

### M0 Scaffold [L]
The solution, every project in the layout, `Directory.Build.props`, an empty test project per project, and `ci.yml` building and testing on `ubuntu-latest` (everything except Flyout / Widget) and `windows-latest` (everything).
- Done: CI is green on both runners, and `dotnet test` passes locally.

### M1 Contracts (WS0) [L]
Specs: U1 D-H1, D-H2 (snapshot shape), D-H3 (pipe messages), D-H4 (action catalog), D-H6 (rules grammar), D-H7 (plugin contract), D-H8 (store layout); B4 (`tasktor.job/1` progress file); U2 (`tasktor.calendar/1`, `tasktor.export/1`, agent hook file `tasktor.agent/1`); U3 (`tasktor.presets/1`); A D-A5 (blocks + hints).
- C# records for every public contract and the block vocabulary (`Metric`, `Sparkline`, `Bar`, `Gauge`, `Table`, `List`, `Progress`, `Tree`, `Alert`, `Badge`, `Countdown`, `Actions`), plus hints. Bindings use `{"bind": "<JSON Pointer>"}`.
- A source-generated `JsonSerializerContext` (AOT-safe).
- `Tasktor.SchemaGen` writes `schemas/tasktor.<name>-1.json`.
- Fixture files for every contract under `tests/fixtures/`.
- **G-schema:** C# → JSON → Python `jsonschema` validate → C#, round-trip equal, for every fixture.
- Done: G-schema runs in CI, and the schemas are current (CI fails if regenerating them changes anything).

### M2 Core shapes and the logic loop [L]
Spec A D-A5 (§2–§7), D-A6; B D-B11.
- `IClock`; `EventChannel` (one logic thread; sources only enqueue); `Scheduler` + `DemandLease`.
- `RingBuffer`, `KeyedTable`, `TreeRollup`, `KindStats`, `LearnedThreshold`, `AppIdentity` + grouping rules (A2.1 / A2.2).
- `Sensitive<T>` + `LogSink` (G-redact).
- Done: unit tests with a fake clock cover every invariant the spec states.

### M3 State, cards and snapshot [L]
U1 D-H2, class table (`StateTracker`, `CardTemplates`, `SnapshotPublisher`); U4 D-G1 (`GlanceText`).
- Done: snapshot JSON matches the fixtures; publishes at most once per second, and only on change; bindings resolve.

### M4 Actions and rules [L]
U1 D-H4, D-H6 (+ amendments from U2, U3, U4).
- `ActionCatalog`, with `surface` tags and `origin`; `OriginPolicy`; `GuardChain`; `ActionRouter` (30 s confirm tokens, off-loop handlers, log).
- `RulesLoader` / `RuleSet` / `RuleEngine`: grammar v1, every state and event and condition listed in U1, `undo`, toasts with ≤ 3 buttons, `run` only from a clicked toast button.
- The reversible-only check for rules; one-time consent for `arrange`; rules may add capture blocks but never lift them.
- Done: table-driven tests for each rule of the grammar and each refusal path.

### M5 Pipe protocol and CLI [L]
U1 D-H3, D-H4 (CLI verbs), D1, D5; U4 H-G5.
- The pipe server and client over `NamedPipeServerStream`, which maps to Unix domain sockets on Linux, so it is testable there. Current-user ACL on Windows [W].
- Messages: hello, subscribe, pull, act, lease.
- CLI verbs and exit codes:
  - `status [--json] [--cards]` (exit 3 when the agent is down);
  - `job …`, `proc …`, `pause|resume …`, `summary`, `open`;
  - `forget …` (with `--since` for capture);
  - `export data`;
  - `settings export|import`, `preset apply`, `uninstall`.
- Destructive verbs need `--yes` and `cli.allow_destructive`.
- `TextRenderer`.
- Done: end-to-end tests that start an in-process agent and drive it through the CLI.

### M6 Storage, privacy, export [L, DPAPI W]
U1 D-H8; U2 D-S7; C (store engine, forget).
- `StoreLayout` (`%LOCALAPPDATA%\Tasktor`, via an abstraction), `SettingsStore` (JSON with comments), `ConsentStore`.
- `InstallKey`: DPAPI on Windows [W], a fake key provider in tests; HKDF.
- `Forgetter`, `SelfUseCounter`.
- `DataExporter` (CSV + JSONL per table + `manifest.json`), plus the Python `tasktor_data.load(folder)` in `worker/`.
- Done: forget and export tests on a temp store, and the Python loader test.

### M7 Plugin host [L, Job Object W]
U1 D-H7 (+ U5's amendment: packs may ship `rules.pack.json` / `config.pack.json`, every entry off until ticked).
- `PluginHost`: discovery, the stdin/stdout protocol, ≤ 64 KB output, schema check, the block subset, ≤ 3 cards and ≤ 3 actions, timeout kill, off after 3 failures, consent pinned to SHA-256 hashes.
- Packs.
- The kill-on-close Job Object (256 MB) adapter [W].
- `examples/plugins/todo-count`.
- Done: host tests with real child processes (plugins as small scripts), including timeout and malformed output.

### M8 System monitor (A) [L logic, W sources]
Spec A, all rows A1–A8 and their decisions.
- Feature classes over fake sources, with the A2.7 verdict.
- Windows sources: PDH, the process table, GPU (WDDM), disk, ports, battery, low-memory events [W].
- Cards: `core.system`, `core.apps`, `core.disk`, `core.battery`, `module.ports`.
- Done: feature tests on recorded or fake samples.

### M9 Long-job watch (B) [L logic, W process control]
Spec B, all rows.
- The job classifier and `JobGate`.
- The `tasktor.job/1` progress files, ETA from `KindStats`, the learned / default badge.
- `PauseCoordinator` (polite pause and freeze), `FrozenRegistry` with crash recovery, the B2.5 finished toast.
- `Win32ProcessControl` [W].
- Card `core.jobs`.
- Done: classifier and coordinator tests, including freeze crash recovery.

### M10 Sources, guards and watches (U2) [L logic, W sources]
Spec U2: every source and feature in its tables.
- First-party plugins `tasktor.calendar` (ICS parsing → `calendar/1`) and `tasktor.repos`.
- The focus timer, `FocusReview`, notes.
- The default rules in `rules.default.json`.
- Done: feature tests per row.

### M11 Workflow learning, T0 (C) [L logic and worker, W hooks]
Spec C: C1–C15 and the Python worker.
- `WinEventSource`, `IdleSource`, `SessionSource` [W].
- `EpisodeBuilder`, `PrivacyGate`, the stores (SQLite WAL; `winsqlite3` on Windows [W], with a test SQLite provider on Linux).
- The Python worker: features, clustering, the pre-registered gates (`prereg.json`), naming, refit and stable ids, the model file. The C# assigner has parity tests against the Python reference.
- `CurrentWorkflow`, card `core.workflow`.
- Done: the whole pipeline on synthetic event logs, with the gates computed and the parity tests green.

### M12 Deep capture, T1 (C16–C22) [L logic, W hooks]
Spec C, D-C19 – D-C22, plus U4 D-G6.
- `tasktor-capture` host, `KeyDecoder`, `KeyRunBuilder`, suppression and redaction, `RawVault` (AES-GCM), the index, actor attribution.
- Capture blocks: pause until, schedule, app / site exclude, tab pause, forget by time range.
- The edge-line indicator window [W].
- `tasktor-nmh` and the browser extension.
- **G-capture-net** (Roslyn analyzer banning `System.Net` in Capture and Shell).
- Done: logic tests on recorded key and UIA streams, and the redaction tests.

### M13 Render stack and surfaces' logic (U4) [L]
Spec U4 D-G1, D-G4, D-G5, D-G7, the card catalog.
- `BindingResolver`, `CardFitter` (S 2 / 1, M 5 / 3, L 9 / 4), `SnapshotClient`, `ActionInvoker`.
- `AdaptiveCardRenderer`: it emits Adaptive Card 1.5 template + data JSON, so it is testable on Linux.
- Every card in the catalog has a provider.
- `ToastNotifier` and the tray menu built from `ActionCatalog` [W].
- Done: fitting tests per size, renderer golden files, and a catalog test that every card id exists.

### M14 Windows UI and windows management [W]
Spec U4 D-G2, D-G3, D-G6, D-G8; spec U3 entirely.
- The WinUI 3 flyout (nine pages, seven dialogs) and the Widgets provider.
- `tasktor-shell`: `WindowList`, `WindowSwitcher`, the hotkey and the opt-in hook, `DwmThumbnails`, presets.
- `WindowPlacer`, `FullscreenSource`, `FancyZonesBridge`.
- Pure logic stays testable on Linux: preset matching, monitor / DIP math, the fullscreen classifier and the switcher's filter / MRU.
- Done: all of it builds on the Windows runner, its logic tests pass on Linux, and every UI check is listed in PROGRESS.

### M15 Quality gates and release [mixed]
Spec U1 D-H11, D-H12, D-H10.
- The gates:
  - G-net (analyzer plus a Windows runtime trace job, [W]);
  - G-capture-net (PE import check, Windows runner);
  - G-schema;
  - G-redact;
  - G-aot (`PublishAot`, warnings as errors, Windows runner);
  - G-private (runs only if the `PRIVATE_NAMES` secret is set).
- `release.yml`: an unsigned portable zip plus MSIX, and `SHA256SUMS.txt`.
- README sections: install, the SmartScreen and antivirus notes, the plugin guide.
- Done: CI is green with every gate wired.

## Definition of done for a cloud run
- M0–M13 are complete and green on Linux, with tests.
- M14–M15 code is present and builds on the Windows CI runner.
- `docs/PROGRESS.md` lists every **[W]** check still to run on a real PC, with exact steps.
- The draft PR to `main` is up to date.
