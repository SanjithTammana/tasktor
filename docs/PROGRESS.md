# Progress

Update this file after every milestone step. The next session starts here.

## Current milestone
M3 State, cards and snapshot. M0–M2 are done.

## Done
- Design signed (`docs/design/`).
- Implementation plan (`docs/IMPLEMENTATION.md`).
- **M0 Scaffold.** `Tasktor.sln` with every project in the layout; `Tasktor.Linux.slnf` (everything except Flyout / Widget) for Linux; `Directory.Build.props` (nullable, warnings as errors, latest language, `EnableWindowsTargeting`, invariant globalization); central package versions in `Directory.Packages.props`; one xUnit test project per src project (`tests/Directory.Build.props` adds xUnit); `ci.yml` builds and tests the filter on `ubuntu-latest` and the whole solution on `windows-latest`. G-net is wired from day one: every `src/` project bans `System.Net*` through `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`src/BannedSymbols.txt`, error RS0030); verified by compiling a probe `HttpClient` in Core.

- **M1 Contracts (WS0).** Records in `Tasktor.Contracts` for every public contract: `tasktor.snapshot/1` (state + cards; block vocabulary `Metric`, `Sparkline`, `Bar`, `Gauge`, `Table`, `List`, `Progress`, `Tree`, `Alert`, `Badge`, `Countdown`, `Actions` with hints; `Value` = literal or `{"bind": "<JSON Pointer>"}`), `tasktor.action/1` (request / result), `tasktor.rules/1`, `tasktor.plugin/1` (manifest, input, output, pack), `tasktor.job/1` (progress file + control file), `tasktor.agent/1`, `tasktor.calendar/1`, `tasktor.export/1`, `tasktor.presets/1`; internal: pipe frames (`hello` / `welcome` / `subscribe` / `delta` / `pull` / `act` / `result` / `lease` / `error`), `WorkerSpec`, `WorkerKind`, `ControlMessage`. One source-generated `ContractsJsonContext` (snake_case, nulls omitted, comments + trailing commas, UTC `Z` timestamps). `tools/Tasktor.SchemaGen` writes `schemas/tasktor.<name>-1.json` with `JsonSchemaExporter`. Fixtures in `tests/fixtures/<schema>/` (+ `invalid/`). **G-schema:** `GSchemaTests` (C#: fixture → record → JSON equals the fixture; schemas equal a fresh generation) + `worker/tests/test_gschema.py` (Python `jsonschema` validates every fixture, rejects every invalid one). Both run in CI.

- **M2 Core shapes and the logic loop.** Contracts gain the seven D-A5 interfaces (`IClock`, `IMetricSource`, `ITableSource<TKey,TRow>`, `IEventSource<T>`, `IProcessControl` with A/B/D-H9 members, `INotifier`, `ICardProvider`) and their value types (`ProcKey`, `MetricKey`, `MetricFrame`, `ColumnSet`, `ProcessRow`, `ToastRequest`, `LearnedValue`, `Sensitive<T>`). Core: `LogicLoop` (one logic thread; `Post` / `EventChannel<T>` only enqueue; `Every` / `At` timers; manual `RunPending` / `RunDue` for fake-clock tests), `Scheduler` + `DemandLease` (shortest interval, column union, release at zero leases), `Cooldown`, `HoldFor`, `RingBuffer<T>` + exact `WindowStats`, `KeyedTable<TKey,TRow>` (diff), `TopN<T>`, `RateCounter`, `TreeRollup` (SoA forest, anchor rule as a parameter), `KeyedHasher` (HKDF per domain + truncated HMAC) + `KindId`, `KindStats` (20-run rings, counters, alarm window, ~300-byte binary form), `LearnedThreshold` (ramp 5 → 20, clamp, sources), `AppIdentity` + `AppGrouper` (A2.1 steps 1–6 + user `grouping` rules, cached per distinct exe), `Glob`, `LogSink` + `FileLogWriter` (7 days, 20 MB, verbose reverts after 24 h, `Sensitive<T>` prints `<redacted>`), `DurationText`. 65 Core tests incl. A1 #3 / #6, A2 #1–#4, A6 #2, B adaptive #2 / #3.

## Next
- M3: `StateTracker` (state tree + JSON Patch deltas), `CardTemplates`, `SnapshotPublisher` (≤ 1/s, on change, atomic file), `GlanceText`.

## Interpretations
- **Linux build set.** CI's Linux job builds `Tasktor.Linux.slnf`, a solution filter without `Tasktor.Flyout` and `Tasktor.Widget` (WinUI / Windows App SDK do not build on Linux). The Windows job builds `Tasktor.sln`.
- **`net10.0-windows` projects run on Linux.** `Tasktor.Windows`, `Tasktor.Agent`, `Tasktor.Capture` and `Tasktor.Shell` target `net10.0-windows` (compiled on Linux with `EnableWindowsTargeting`); their test projects target the same TFM and run on Linux. Only code paths that call Win32 need Windows; everything else is tested here.
- **G-net from M0.** The banned-API analyzer (planned for M12/M15) is on from the start for every `src/` project; it costs nothing and stops accidents early. Network use lives only in plugins (outside `src/`).
- **No Flyout / Widget test projects yet.** Their testable logic lives in `Tasktor.Hosts.Common` (renderers, fitter), which has its own test project.
- **Schema ids.** `$id` is `urn:tasktor:schema:<file>` so the repo names no web domain it does not own; files point `$schema` at the shipped schema file (relative path) for editor autocomplete.
- **One schema file per root record.** `tasktor.action/1` is two files (`action-request-1`, `action-result-1`); `tasktor.plugin/1` is four (`plugin-1` manifest, `plugin-input-1`, `plugin-output-1`, `pack-1`). The contract id stays the same; the file name says which record.
- **Conditions and rule actions are flat records.** The rules grammar picks a condition / action kind by which key is present (`metric`, `learned`, `all`, …), not by a discriminator, so `Condition` and `RuleAction` are records with all keys optional; `RulesLoader` (M4) enforces exactly one kind per object. Value-states use `{"state": "fullscreen", "is": "exclusive"}`.
- **Action origins.** `widget`, `flyout`, `tray`, `toast`, `cli`, `rule`, plus `shell` for `tasktor-shell` (U3). Plugin card buttons arrive with the viewer's origin and the `plugin.action` action, which only reaches the plugin's own declared actions.
- **Args and targets are string maps** (`{"mode": "polite"}`, `{"pid": "4242"}`) so every origin (CLI included) sends the same shape.
- **Grouping rules live in `rules.json`.** A2.2 says grouping rules are `rules.json` entries, so `tasktor.rules/1` gained an additive `grouping` list (`match {exe|path|package} → group_as | attach: parent | never`).
- **The seven interfaces live in Contracts** (D-A5 §4) with their value types, so plugins' SDK and viewers share them; `IBlockRenderer` stays in the viewer hosts.
- **Own log sink instead of `Microsoft.Extensions.Logging`.** H6 asks for an AOT-safe logger without personal data; a 150-line `LogSink` with typed `LogField`s (primitives or `Sensitive<T>` only) keeps the agent dependency-free and makes redaction structural.
- **`AppGrouper` system bucket:** a process with no readable path that is not the user's own, plus PIDs 0 and 4, goes to "System / services" (A2.1 step 6).
- **Package pins.** xUnit 2.9.3, Test SDK 17.14.1, BannedApiAnalyzers 4.14.0, Windows App SDK 2.5.1 (the version the U4 sources cite).

## Needs Windows verification
- **M0 / CI:** the `windows` job builds `Tasktor.sln` including the WinUI 3 Flyout and the Widget project. **Verified green on `windows-latest`** (run 41963321717). Keep watching it as UI code lands.

## Blockers
- **.NET install script blocked.** `dotnet-install.sh` cannot reach `builds.dotnet.microsoft.com` (the session proxy returns 403). Worked around: `apt-get install dotnet-sdk-10.0` from the Ubuntu archive (SDK 10.0.114, which satisfies `global.json` 10.0.100 / latestFeature). `scripts/cloud-setup.sh` now falls back to apt. Not blocking.
