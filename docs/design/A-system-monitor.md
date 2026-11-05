# Tasktor — A. System monitor (feature spec)

**Status:** signed. A1–A8 settled, A9 folded, A10 cut; stack C# + Python; D-A1–D-A6 decided.
**Reference low end:** i7-1355U (10 cores / 12 threads), 12 GB RAM, Iris Xe, one monitor, C: 91% full.

Conventions: **[verified: source]** = checked against Microsoft docs or a local probe this session; **[unverified]** = still a claim, to be checked in the named phase. Sizes: **S / M / L** = Widgets-board small / medium / large card; **Flyout** = own window (G2). **** = proposed this session, not yet accepted.

## Cross-cutting decisions
- **D-A1 History tick.** `tasktor-core` reads system totals (CPU, RAM, GPU, disk, network) once a minute, always, and keeps the last hour in memory (a ring buffer, not written to disk). Per-process data is still read only while a viewer is open or a rule needs it. This amends the Frame's "sample only while viewed" rule (C1.1 / H5) for system totals; listed under Changes for the catalog.
- **D-A2 Catalog shape.** A9 folded into A1.3/A2.3; A10 cut; proposed A11 → A2.7, A12 → A1.6.
- **D-A2b:** A4 folded into A3; A8 moved from "later" to W1, Core (laptops only).

### D-A3 Efficiency: Tasktor must stay light
Concern: always-on tracking must not noticeably raise RAM or CPU on a 12 GB laptop that already sits near full memory (863 MB available at the A1 probe).

**Where memory could go, and the design that bounds it** (sizes are estimates until A0 measures them **[unverified]**)
| Part | Risk | Design |
|---|---|---|
| History tick (D-A1) | grows over time | fixed 60-slot ring × ~12 numbers ≈ a few KB; never grows, never written to disk |
| Per-process data | ~600 rows × several fields | held only while a viewer is open; dropped (buffers freed) ~30 s after the last viewer closes |
| Process snapshot cost | ~600 `OpenProcess` calls per sample | prefer one `NtQuerySystemInformation` call when A0 shows it is cheaper; reuse buffers between samples, no per-sample allocation |
| PDH queries (GPU Engine has ~500 instances) | PDH keeps query state in memory | open per-process and GPU-engine queries only while viewed; the tick uses a small, fixed counter set; close queries when idle |
| RAM-pressure detection (A6) | polling | wait on Windows' system-wide `LowMemoryResourceNotification` event instead of polling; it is "a system-wide event" a thread can wait on **[verified: CreateMemoryResourceNotification doc]**; the per-process offender is read only when it fires |
| Folder scan (A5) | a whole-disk tree in memory | stream and aggregate; keep only the top N children per level; cache the result on disk, not in RAM |
| Event store (C1.7) | SQLite page cache | small fixed `cache_size`, batched writes, nothing held in RAM beyond the open episode |

**Tasktor throttles itself**
- The agent marks itself EcoQoS ("reducing CPU frequency or using more power efficient cores", for work "not contributing to the foreground user experience") and low memory priority, whose pages Windows trims first **[verified: SetProcessInformation doc]**.
- No busy loops: every sampler is a timer or an event wait; with no viewer open the agent wakes about once a minute plus on Windows events.
- Under memory pressure (the same Windows event) Tasktor sheds its own caches first: drops per-process buffers, closes PDH queries, defers refits.

**Budgets**
| State | Agent private memory | Agent CPU |
|---|---|---|
| Idle (no viewer, tick only) | ≤ 15 MB | ≤ 0.1% average |
| Viewer open (1–2 s sampling) | ≤ 30 MB | ≤ 1% average |
| Folder scan running | ≤ 60 MB | BelowNormal, any |
Numbers are targets to confirm against the A0 skeleton on the reference PC **[unverified]**.

**Self-accountability**
- `tasktor-core` appears in its own A2 list like any other app (not hidden).
- `tasktor status --json` reports its own private memory, CPU and wake-ups per minute.
- A built-in rule logs (and on a second breach, notifies) if Tasktor exceeds its own budget.

**Acceptance**
1. Idle 8 h on the reference PC with no viewer: private memory stays within budget and does not grow by > 1 MB over the run (no leak) (manual + logged).
2. Open and close the flyout 50 times: agent memory returns to the idle baseline within 1 MB (test).
3. CI perf test on a 4-core / 8 GB Windows runner holds the idle budget (H5).
4. When `LowMemoryResourceNotification` is signalled, the agent's private memory drops (test with a forced signal / mock).

## Architecture (D-A4, 2026-11-04: object-oriented, minimum time and space without losing accuracy)

### D-A5 Data-first design
**Method:** list the data Tasktor collects, sort it by **shape**, give each shape one set of reusable classes with behaviour, and only then cut layers. Interfaces appear only where implementations really differ.

**1. What A collects, by shape**
| Shape | Data in A | Key | Changes how |
|---|---|---|---|
| **Series**: a number over time | CPU %, per-core %; RAM used / available; commit; GPU % and memory per adapter; disk active % and bytes/s per disk; network bytes/s per adapter; free space per volume; battery % | metric id + instance (e.g. `disk.active`, `C:`) | sampled; rates are deltas over an interval |
| **Table**: rows that come and go | processes; listening ports; volumes; GPU adapters, disks and NICs as instances | pid + creation time; port + address; volume id | diffed between samples (added / removed / changed) |
| **Tree**: rows with a parent | process parent tree; folder sizes | child → parent | totals roll up to a root |
| **Event**: pushed changes | AC/DC and battery-saver changes; low memory; process start/exit (from the table diff) | type + time | arrives from Windows or from a diff |

The same four shapes cover the other sections: B job rows (table + series), C foreground and idle (events) plus process start/stop (table diff), E sources (series or table), I7 space report (tree), I14 connectivity (series + events). **Shapes are the unit of reuse, not resources.**

**2. Reusable classes per shape (concrete classes with behaviour; no interface unless noted)**
| Shape | Class | Behaviour | Cost |
|---|---|---|---|
| Series | `MetricFrame` | every metric value at one timestamp, stored column-wise | O(m) per sample, m fixed |
| Series | `RateCounter` | turns raw cumulative counters (bytes, CPU time) into per-second rates; shared by network, per-process CPU and disk bytes | O(1) |
| Table | `KeyedTable<TKey, TRow>` | holds the current rows; `Diff()` gives added / removed / changed; reuses buffers | O(n) per sample via hashing |
| Table | `TopN<T>` | exact top N by any column without a full sort | O(n log N) |
| Tree | `TreeRollup` | resolves each row's root once (memoised) and sums columns up the tree; **the same class groups processes into apps (A2) and sums folder sizes (A5, I7)** | O(n) |
| Event | `EventChannel<T>` + `Cooldown` + `HoldFor` | delivery, "at most once per X", "only if true for D" | O(1) per event / rule |
| Cross | `GuardChain` | runs each process action through the A3 guards | O(guards) |

**3. Lean interface set: interfaces only at real seams.** An interface exists only where (a) Windows code meets testable logic, (b) another process meets the agent (viewers, plugins), or (c) time must be faked. There are **no per-resource interfaces** (`ICpu`, `IRam`, `IGpu`, `IDisk`): those differ in data, not behaviour, so they are `MetricId` rows filled by a source.
| Interface | Seam | Real implementations (Windows) | Test implementations |
|---|---|---|---|
| `IMetricSource` | OS → series | `PdhMetricSource` (CPU, RAM, commit, GPU, disk), `NetworkMetricSource` (`GetIfTable2`), `VolumeMetricSource`, `PowerMetricSource` | fake values, replayed recordings |
| `IEventSource<T>` | OS → events | `PowerEvents`, `LowMemoryEvents` | scripted events |
| `INotifier` | logic → toast | `ToastNotifier` | recording fake |
| `ICardProvider` | features / plugins → viewers | every feature class; the plugin host adapter for out-of-process Packs | snapshot asserts |
| `IClock` | time | system clock | fake clock |
Seven interfaces in total. Features share an abstract base class `Feature` (Id, Tier, Enable/Disable to take and release leases, Rules), a real is-a relationship, and implement `ICardProvider`. One API family = one class, so a single PDH query object serves CPU, RAM, GPU and disk together, which is also the cheapest way to read them.

**4. Layers, cut from the above**
```
Tasktor.Contracts   records (MetricId, MetricFrame view, ProcessRow, Listener, Volume, PowerState, Card, Action) + the 7 interfaces
                    + U1 records: Snapshot; Card / Block / Value (Binding); ActionRequest / ActionResult / ActionClass / Origin;
                      RulesFile / Rule; PluginManifest / PluginOutput / PackManifest; AgentHookFile; WorkerSpec; ControlMessage;
                    → shared by agent, widget, flyout and the plugin SDK; no Windows code
Tasktor.Core        the shape classes above + Scheduler, RuleEngine, AppIdentity, GuardChain + feature classes (A1 SystemTotals, A2 Apps, A5 DiskGuard, …)
                    → pure logic; builds and tests on Linux CI
Tasktor.Windows     the Windows implementations of the 7 interfaces
Hosts               Tasktor.Agent (hand-written composition root, Native AOT) · Tasktor.Widget · Tasktor.Flyout · Python worker (WS0 JSON)
Tests               Tasktor.Core.Tests (contains all fakes, replays and the fake clock) · Tasktor.Windows.Tests (local-only integration)
```
Changes from the first cut: Fakes merged into the test projects; Features merged into Core (feature classes are small compositions of shape classes); per-resource interfaces dropped. **Four runtime assemblies plus two test projects.**

**5. Native AOT constraints, unchanged:** no reflection-based dependency injection (one composition root); source-generated JSON; plugins out-of-process, because Native AOT has "no dynamic loading" **[verified: learn.microsoft.com native-aot]**.

**6. How the rest of the project maps onto this (for the parent / sibling sessions)**
| Section | New sources (interface implementations) | Reuses |
|---|---|---|
| D Integration | CLI / local API and plugin host as `ICardProvider` adapters | snapshot + card records |
| E Glance | ICS / SMTC / weather as `ITableSource` or `IEventSource` | `Scheduler`, `ICardProvider`, `INotifier` |
| F Windows | `WindowEvents : IEventSource`; window placement as an action interface if needed | `AppIdentity` |
| G Surfaces | render `ICardProvider` output | Contracts records |
| H Platform | composition root, settings files, budgets, CI | `SelfMonitor` (a feature over the process table, own pid) |
| I Tailored | mostly rules and features | `TreeRollup` (I7), `RuleEngine`, `IProcessControl`, `PowerEvents` |

- **One logic thread.** All Core state (tables, rules, learners, features) is touched by a single agent loop, so Core has no locks.
- **Sources only enqueue.** OS callbacks and waits (PDH reads, power and low-memory events, process-exit waits, file-change completions, worker stdout) post an item onto the loop's `EventChannel`; they never touch Core state. A's sources follow this too.
- **Actions run off the loop.** `IProcessControl` calls (close, end, priority, freeze, thaw …) run on a worker and post their result back as an event, so a slow call never stalls sampling.
- **Persistence** (settings, learned state): the loop takes a byte copy; a worker writes it.

### D-A6 Trees and presentation
**Which trees.** `TreeRollup` handles **rooted n-ary forests**, not binary trees: any number of children per node and several roots.
- **Process forest:** parent pointers from the snapshot, many roots (orphans whose parent exited). It is acyclic by construction, because a parent only counts if it was created before the child (PID-reuse guard).
- **Folder tree:** one root per scanned folder; junctions and symlinks are not followed, so there are no cycles.
- **Representation (struct-of-arrays, 3–4 ints per node):** `parent[i]`, `firstChild[i]`, `nextSibling[i]` (left-child / right-sibling, so children are listed without a list per node), and `anchor[i]`, a memo of the node's rollup target.
- **Cost:** anchors resolved once per node (memoised) O(n); sums in one reverse pass O(n); top-N children per node with `TopN`. Folder scans aggregate post-order as the walk returns, so memory is O(depth + kept rows).
- **Not covered:** UI Automation and window trees (C2, F). Those are navigated lazily and never rolled up; if F needs it, they reuse the same array representation, but not `TreeRollup`.

**Design cues: a semantic contract plus a renderer layer.** `ICardProvider` stays **semantic**: it says *what* to show and how important it is, never colours or pixels. A separate **renderer layer in each viewer** decides the look, so built-ins and third-party plugins render consistently and follow Fluent, light/dark and high contrast (H4).
- **Card = ordered blocks + hints.** The block vocabulary lives in Contracts and maps 1:1 onto the data shapes:
  | Data shape | Blocks |
  |---|---|
  | Series | `Metric` (value + unit + trend), `Sparkline`, `Bar`, `Gauge` |
  | Table | `Table` (top-N rows), `List`, `Progress` |
  | Tree | `Tree` (drill-down) |
  | Event | `Alert` (toast), `Badge` |
  | cross | `Countdown` (E1/E2), `Media` (E4), `Actions` (buttons) |
- **Hints (semantic only):** severity (normal / warn / critical), priority (what survives on a small card), unit, trend, an icon key from a fixed icon set, and an accent *role* (not a hex colour).
- **Rule:** a new block kind is a project-level change, like a new shape. Sections compose existing blocks first.

**Slot map: where every section plugs in, and nothing else changes**
| Slot | Mechanism | Who uses it |
|---|---|---|
| New behaviour | a `Feature` subclass + `ICardProvider` | every section |
| New decision | a rule in `rules.json`, or a new condition class | B2, I2–I4, I11, I13 |
| New action | `IProcessControl` / `INotifier`, or an action handler on a card | B3, I3, I4, I5 |
| New look | blocks + hints (provider side); renderers (G) | all; G owns renderers |
| Outside code | a Pack plugin = out-of-process `ICardProvider` through the plugin host | D, a private pack |

### Where A sits
Section A is not a separate program. It is a set of objects inside `tasktor-core` (Frame C1), and most of them are shared with other sections.

```
 Windows APIs ──► Platform adapters ──► Samplers ──► Domain model ──► Snapshot publisher ──► Viewers (widget, flyout, tray, CLI, plugins)
 (PDH, Toolhelp/   (one per API family,   (demand-     (SystemTotals,   (WS0 schema, pipe +
  NtQSI, IP Helper,  trait-backed, fake     driven by    ProcessTable,     snapshot.json)
  power events…)     in tests/cloud)        Scheduler)   AppGroups, …)          ▲
                                               │              │                 │
                                               ▼              ▼                 │
                                         RingBuffer      Rule engine ──► Actions (Toast, ProcessController + Guards)
                                         (tick history)  (WS4)
```
Frame mapping: adapters + samplers = C1.1; rule engine + toasts = C1.4; publisher + pipe = C1.8; viewers = C3. A stores nothing on disk except settings and rules (C1.7 `config.json`, `rules.json`).

### OO model (C#, after the OQ12 spike)

_Names in this table predate D-A5's data-first cut. D-A5 wins where they differ: `ProcessTable` → `KeyedTable` + `ProcessTableSource`, `Grouper` → `AppIdentity` + `TreeRollup`, `ProcessController` → `IProcessControl`, `Platform` traits → the three source interfaces._

| Object (trait / type) | Responsibility | Used by A | Reused by |
|---|---|---|---|
| `Platform` traits: `CounterSource`, `ProcessSource`, `PowerSource`, `MemorySignal`, `VolumeSource`, `ListenerSource`, `DirWalker` | One wrapper per Windows API family; the only code that calls Win32 | all | every section (fakes for tests) |
| `Scheduler` + `Demand` | Subscriptions "sampler X at interval Y" with reference counts; runs each sampler at the **shortest requested interval**, stops it and frees its buffers at zero subscribers | D-A1 tick, viewer 1–2 s, A6 escalation 5 s | B, C, E, I (any periodic work) |
| `Sampler<T>` | Reads one source into a reusable buffer, computes deltas against the previous read | A1, A2, A5, A7 | B (job rows), C (process start/stop diff) |
| `ProcessTable` | Current processes, stored column-wise (struct-of-arrays) | A2, A3, A6, A7 | B, C, I1, I2, I3 |
| `AppIdentity` + `Grouper` | exe/package → app id + display name; parent tree → `AppGroup`s via ordered rules | A2 | **C (cluster features must use the same app ids)**, B, I1, I2, I5 |
| `RuleEngine`: `Condition` + `HoldFor` + `Cooldown` + `Action` | Evaluates `rules.json` on each relevant sample | A5, A6, A8.2 | B2, I2, I3, I4, I11, I13, I14 |
| `ProcessController` + `Guard` chain (`OwnUser`, `Integrity`, `Protected`, `Denylist`, `IdentityRecheck`) | Close / End / tree / priority / EcoQoS, each request passed through every guard first (chain of responsibility) | A3, A6, A7 | B3, I3, I4 |
| `Notifier` | Toasts with buttons, cooldown bookkeeping | A5, A6, A8 | B2, E, I |

### Data sources (one place)
| Data | Adapter | Windows source | Admin | Push or poll |
|---|---|---|---|---|
| CPU, RAM, commit, disk, GPU totals + GPU per pid | `CounterSource` | PDH counters (A1 table) | no | poll (tick / viewer) |
| Process list, parents, CPU time, memory | `ProcessSource` | Toolhelp32 or `NtQuerySystemInformation` + `PROCESS_QUERY_LIMITED_INFORMATION` | no | poll (viewer / rule) |
| Network bytes | `CounterSource` | `GetIfTable2` | no | poll |
| Free space | `VolumeSource` | `GetDiskFreeSpaceEx` | no | poll (tick) |
| Biggest folders | `DirWalker` | `FindFirstFileEx` walk | no | on request |
| Listening ports | `ListenerSource` | `GetExtendedTcpTable` (OWNER_PID_LISTENER) | no | poll (card open) |
| Low memory | `MemorySignal` | `CreateMemoryResourceNotification` | no | **push** (wait) |
| AC/DC, battery saver, % | `PowerSource` | `GetSystemPowerStatus` + `RegisterPowerSettingNotification` | no | **push** |

### Time and space per operation (n ≈ 600 processes, u ≈ distinct exe names, e ≈ 500 GPU engine instances, k ≈ 30 listeners, r = rules, d = directory entries)
| Operation | Naive | Design | Exact? |
|---|---|---|---|
| Totals sample | — | O(1) time, O(1) space; ring O(60) fixed | yes |
| Process snapshot | n `OpenProcess` calls + fresh allocations | O(n) with **one** system call if A0 confirms `NtQuerySystemInformation` is cheaper; buffers reused, zero steady-state allocation | yes |
| CPU % per process | re-scan previous list per row, O(n²) | hash map keyed by (pid, creation time) → O(n); the creation time also guards PID reuse | yes |
| Grouping by parent tree | walk to the root per process, O(n·depth) | memoised root per pid (each node resolved once) → O(n) | yes |
| Grouping rules (patterns) | match every rule against every process, O(n·r) | rules resolved **once per distinct exe** and cached (594 processes but far fewer distinct exe names) → O(u·r) on change, O(1) per process after | yes |
| Exe names / paths | a string per process per sample | interned once in a string table; rows hold small integer ids | yes |
| Top-N apps for cards | full sort O(n log n) | bounded heap O(g log N), g = groups, N ≤ 5; the full sort happens only when the flyout table is open | yes (exact top N) |
| GPU per process | — | one pass over e instances into the pid map, O(e) | yes |
| "Holds for D" in rules | store the window and re-check it, O(D) | running "above since" timestamp per rule, O(1) time and space | yes |
| Growth ("+600 MB in 1 min") | keep per-process history | two points: value at escalation start and now, O(1) per group | yes for the reported span |
| Rule evaluation | every rule every sample | each rule subscribes to the metrics it reads; only rules whose inputs changed run | yes |
| Folder scan | full tree in memory, O(d) space | streaming depth-first walk, O(d) time; space O(depth + kept), keeping the top 20 children per level for 4 levels; deeper drill-downs rescan that subtree on demand | yes for what is shown |
| Snapshot to viewers | serialise all processes every sample | cards get top-N only; the full table is **pulled** over the pipe while the flyout is open; publish only when values change | yes |
| Power, low memory | poll | OS events: no wake-ups between changes | yes |

Quality guard: none of these trade accuracy for speed. Every "Design" column is exact for what the user sees. The one heuristic, matching Task Manager's GPU % via the busiest engine, is already flagged (A9).

### Reuse summary for sibling sections
**Not A-specific:** the shape classes and interfaces are project infrastructure; A is just their first user (see D-A5 and the "Shared, not A-owned" catalog entry). Build A's objects as a shared core library (WS1 + WS2), not as A-only code: B, C, E, H and I compose the same `Scheduler`, `ProcessTable`, `Grouper`, `RuleEngine`, `ProcessController` and `Notifier`. The one hard dependency to coordinate: **C (learning) must take app ids from `AppIdentity`**, so a workflow cluster and the A2 row for the same app use one identity.

## Subfeature outline (draft, 2026-11-04)
Each line gets detailed (surface, data source, sampling, defaults, actions, edge cases, acceptance) in its feature section as we go.

**A1. Totals at a glance**
- A1.1 CPU: total %; per-core in the flyout
- A1.2 RAM: used / total, available, commit charge
- A1.3 GPU: % per adapter (busiest engine), GPU memory (A9 folded in)
- A1.4 Disk: activity % and read/write rate per physical disk
- A1.5 Network: up/down rate per adapter
- A1.6 Sparklines + recent history (former A12): last 60 s live; last hour with spike markers if the history tick is accepted
- A1.7 Warn/critical colour thresholds, editable

**A2. Processes grouped by app**
- A2.1 Grouping engine: child processes (browser renderers, Electron, WebView2) collapse into their host app
- A2.2 Editable grouping rules
- A2.3 Columns: CPU, RAM, GPU (A9), disk; per-process network **[unverified, likely limited]**
- A2.4 Sort, top-N on the cards, expand a group to its processes
- A2.5 App icon + friendly name
- A2.6  "System / services" bucket so OS processes do not crowd the list
- A2.7  "Why is it slow" verdict (former A11): one line on the S card naming the saturated resource and the top app group, e.g. "RAM 92%: Chrome 3.1 GB"

**A3. Per-process actions (A4 guards folded in)**
- A3.1 End process (two-step confirm)
- A3.2 End tree / end whole app group
- A3.3 Below-normal priority, with restore
- A3.4  Efficiency mode (as in Task Manager) **[unverified API]**
- A3.5 Open file location
- A3.6 Open in Task Manager
- A3.7 Guards: own-user processes only; elevated, system and protected rows shown disabled with a reason; critical-process denylist
- A3.8 Result feedback (done / denied / already exited)

**A5. Disk guard**
- A5.1 Free space per fixed volume
- A5.2 Threshold alert (% and GB) with toast
- A5.3 Biggest-folders scan on demand (low priority, cancellable)
- A5.4 Open in Explorer / open Windows Storage settings

**A6. Pressure alerts**
- A6.1 RAM-pressure signal (available RAM, commit vs limit)
- A6.2 Alert names the offending app group and its growth
- A6.3 Toast actions: end, lower priority, snooze
- A6.4 Hysteresis and cooldown (no alert spam)
- A6.5  Generalise to sustained CPU / disk saturation

**A7. Dev ports (Module)**
- A7.1 Listening TCP ports with owning process
- A7.2 Filter: localhost / dev-range ports by default
- A7.3 Open `http://localhost:<port>` in the browser
- A7.4 Kill the owner (A3 guards apply)
- A7.5 Port-conflict hint ("3000 is taken by node, PID …")

**A8. Battery (laptops only)**
- A8.1 Percent, charging state, time remaining
- A8.2 Unplugged-under-heavy-load warning
- A8.3 Hidden entirely on PCs with no battery

**A9. Per-process GPU** → folded into A1.3 / A2.3. Coverage: Windows 11's minimum spec requires a WDDM 2.0 driver **[verified: microsoft.com/windows/windows-11-specifications]**, and Task Manager's GPU data needs WDDM 2.0+ **[verified: devblogs.microsoft.com/directx/gpus-in-the-task-manager]**, so every supported Win11 PC with a real GPU driver has it. Gaps: VMs / Microsoft Basic Display Adapter and Win11 installed on unsupported hardware → GPU column and row hidden, not shown as 0 **[unverified per case; check in W1]**.

**A10. CPU temperature** → **cut**. Not readable without admin on the reference PC, firmware-dependent, not the die sensor. A Pack may add it.

A11 and A12 (proposed) were folded into A2.7 and A1.6 on 2026-11-04.

## A1. CPU, RAM, GPU, disk, network at a glance
**Status:** settled. S card = CPU / RAM / GPU + A2.7 verdict line.
- RAM leads with **used %** on every card; available GB and commit shown in M/L detail and the flyout.

**What the user sees**
| Surface | Content |
|---|---|
| S | CPU %, RAM %, GPU % as three numbers with warn/critical colour, plus the A2.7 verdict line |
| M | Five rows (CPU, RAM, GPU, disk, network): value, bar, last-hour sparkline from the tick (A1.6) |
| L | M + the top 5 app groups from A2 |
| Flyout | Live 1 s charts; per-core grid; one row per physical disk, network adapter and GPU; GPU engines on expand; spike markers on the hour view |

**Data sources (all non-admin)**
| Sub | Source | Evidence |
|---|---|---|
| A1.1 CPU | PDH `\Processor Information(_Total)\% Processor Time` (bounded 0–100); per-core `\Processor Information(*)`. `% Processor Utility` (frequency-scaled, can exceed 100) kept as a hidden alternative | Both read non-admin on the reference PC **[verified: local probe]**. Which counter Task Manager's tabs use today is contested across sources (Q&A, 2025 reports of a change) **[unverified]**: acceptance compares against Task Manager rather than assuming |
| A1.2 RAM | `\Memory\Available MBytes`, `\Memory\Committed Bytes`, `\Memory\Commit Limit`; total from `GlobalMemoryStatusEx` | Counters read non-admin **[verified: local probe]**; API **[unverified, check in A0]** |
| A1.3 GPU | `\GPU Engine(*)\Utilization Percentage` summed by adapter LUID, busiest engine type = adapter %; `\GPU Adapter Memory(*)\Dedicated Usage` / `Shared Usage` | Read non-admin **[verified: local probe]**; needs WDDM 2.0+, which Win11's minimum spec requires **[verified: microsoft.com Win11 specs; DirectX blog]** |
| A1.4 Disk | `\PhysicalDisk(*)\% Idle Time` (active = 100 − idle), `Disk Read/Write Bytes/sec` | **[verified: local probe]** |
| A1.5 Network | Per-adapter byte counters (`GetIfTable2` / `MIB_IF_ROW2`, the API under `Get-NetAdapterStatistics`), rate = delta / interval | Cmdlet read non-admin **[verified: local probe]**; the Win32 call itself **[unverified, check in A0]** |

**Sampling**
- Tick (D-A1): every 60 s, always, totals only; 60-point ring buffer in memory.
- Widget open: every 2 s (actual widget refresh rate is a W0 unknown). Flyout open: every 1 s. Stops within one interval of the last viewer closing.
- PDH rate counters need two reads; the first value appears one interval after a viewer opens, filled meanwhile from the tick.

**Defaults and settings**
- Rows shown: all five; each can be hidden. Network in bytes/s (setting: bits/s).
- Colour thresholds (settings, not rules): CPU warn 80 / crit 95; RAM warn 85 / crit 95; GPU warn 85 / crit 95; disk active warn 80 / crit 95. Colour changes only after the value holds for 10 s (no flicker).
- Alerts on these values are not here: they are rules in A6.

**Actions:** none on A1 itself; clicking a row opens the flyout on that resource.

**Edge cases**
- Several GPUs: one row per physical adapter (LUID). Adapters with no engines in use and no memory (virtual / indirect display adapters) are hidden. The reference PC reports two LUIDs, one with 8 KB shared memory, i.e. exactly this case **[verified: local probe]**.
- No GPU counters (VM, Basic Display Adapter, unsupported install): GPU row hidden, not 0.
- Network: adapters with zero traffic since boot (e.g. VPN virtual adapters, as on the reference PC) collapse under "other".
- Hybrid CPUs (P + E cores, as on the i7-1355U): per-core grid labels core type only if Windows exposes it **[unverified]**.
- Removable disks appear / disappear without errors.

**Acceptance**
1. Under a steady load, each A1 value is within ±5 points of Task Manager's Performance tab over a 60 s average (manual, reference PC).
2. Runs from a standard-user token with no elevation prompt (test).
3. After 60 min with no viewer open, opening the flyout shows 60 tick points immediately (test with a fake clock).
4. Tick-only CPU cost of `tasktor-core` fits the H5 idle budget (< 1%); measured on the reference PC in A0.
5. A mocked PDH with no GPU instances hides the GPU row; a mocked second adapter with zero usage is hidden (unit test).
6. Closing the last viewer stops 1–2 s sampling within one interval (test via a sample counter).

## A2. Processes grouped by app
**Status:** settled. Tool processes count **under the launching app** (rule step 3: node, python, cmd … attach to their nearest non-launcher ancestor; expand the group to see them). Individual jobs are listed separately by section B's job card.

**Reference-PC probe (2026-11-04, non-admin):** 594 processes. Top exe counts: conhost 120, svchost 100, chrome 29, node 29, cmd 23, claude 23, msedgewebview2 20. 176 processes had no readable path through WMI (system services and elevated processes). WebView2: the root `msedgewebview2.exe` is parented by its host app (ms-teams, WidgetBoard, SearchHost) and the other 17 by that root, so walking up the tree finds the host **[verified: local probe]**.

**What the user sees**
| Surface | Content |
|---|---|
| S | The A2.7 verdict line only |
| M | Top 3 app groups by the resource currently under most pressure (else CPU) |
| L | Top 5 groups: icon, name, CPU %, RAM, GPU %; process count badge ("Chrome ×29") |

**Data sources (non-admin)**
- Process list + parent PID + exe name: Toolhelp32 snapshot. No handle needed, so even inaccessible processes get a name **[unverified API detail, check in A0; the WMI equivalent probed above]**.
- Path, CPU time, memory: open each process with `PROCESS_QUERY_LIMITED_INFORMATION`. It covers `QueryFullProcessImageName` and `GetProcessMemoryInfo`, and unlike `PROCESS_QUERY_INFORMATION` it is not on the protected-process deny list **[verified: learn.microsoft.com process-security-and-access-rights; GetProcessMemoryInfo]**. `GetProcessTimes` and `GetProcessIoCounters` with that right **[unverified]**.
- RAM column = private working set (Task Manager's default "Memory" column) **[unverified which field matches; check in W1]**.
- GPU column: A1.3 counters keyed by `pid_<pid>`.
- Display name: the exe's version-info description; packaged apps use their package display name. Icon from the exe.
- Cost risk: ~600 opens every 2 s on the low end. Fallback: `NtQuerySystemInformation(SystemProcessInformation)`, one call for all processes. Microsoft marks it as subject to change **[unverified]**. A0 measures both.

**Grouping engine (A2.1 / A2.2)**
1. Same exe child → parent (Chrome renderers, Electron helpers).
2. Known helper exes (`msedgewebview2`, `conhost`, `crashpad_handler`, …) → the nearest ancestor that is not a helper.
3. Pending Q-A2: what tool processes (node, python, cmd …) attach to.
4. Stop walking at launchers (explorer, services, svchost, sihost, userinit): those never swallow what they start.
5. PID reuse guard: a parent counts only if it was created before the child.
6. No readable path and not your user → "System / services".
Rules live in `rules.json` (H10) as ordered `match exe/path/package → group as / attach to parent / never group` entries. Defaults ship read-only; user rules go first.

**Sampling:** only while a viewer is open (2 s widget, 1 s flyout) or a rule needs per-process data (A6). Not on the 60 s tick (D-A1).

**Defaults and settings:** sort by CPU; show the top N (M 3, L 5); System bucket collapsed; "show only my processes" off.


**Edge cases:** processes that exit between list and open (drop silently); 32-bit processes; Store / packaged apps with several exes; two instances of one app (one group, count badge); elevated copies of your own apps (grouped by name, flagged "admin", greyed in A3).

**Acceptance**
1. Fixture tree (Chrome + renderers, Teams + WebView2, terminal + node) groups as the rules say (unit test on recorded snapshots, including the reference-PC one).
2. Group CPU / RAM equals the sum of its members (unit test).
3. A process that exits mid-sample produces no error and no ghost row (test).
4. A user rule overrides a default rule (test).
5. A full per-process sample of ~600 processes fits the H5 budget at 1 s cadence on the reference PC (measured in A0).
6. Chrome's and Teams' group RAM is within 10% of Task Manager's Processes tab (manual).

## A3. Per-process actions (A4 guards folded in)
**Status:** settled. A3.0 Close-first accepted: the group menu leads with **Close**, and **End** is the hard stop. **Never elevate:** Tasktor never shows a UAC prompt; admin rows are greyed with "Runs as admin — use Task Manager".

**What the user sees**
| Surface | Content |
|---|---|
| S | None |
| M / L | Per top-group row: **End** and a "…" menu (Lower priority, Efficiency mode, Open file location, Open Task Manager). Disabled rows show a lock and a one-line reason |
| Flyout | The same on groups and on single processes, plus the result line ("Ended Chrome: 29 processes", "Access denied: runs as admin", "Already exited") |

**Actions and Windows mechanism**
| Sub | Action | Mechanism | Evidence |
|---|---|---|---|
| A3.0  | Close (graceful): ask the app's windows to close, so unsaved work prompts appear | post `WM_CLOSE` to the group's top-level windows; fall back to End after a timeout only if the user confirms | **[unverified, design]** |
| A3.1 | End process | `TerminateProcess`, needs `PROCESS_TERMINATE` | **[verified: process-security-and-access-rights]** |
| A3.2 | End tree / whole group | descendants from the A2 snapshot, ended leaf-first, PID-reuse guard re-checked just before each call | design |
| A3.3 | Below-normal priority, restore | `SetPriorityClass`, needs `PROCESS_SET_INFORMATION`; original class remembered for restore | **[verified: process-security-and-access-rights]** |
| A3.4 | Efficiency mode | `SetProcessInformation(ProcessPowerThrottling, EXECUTION_SPEED)` = EcoQoS ("reducing CPU frequency or using more power efficient cores"), plus idle/below-normal priority; needs `PROCESS_SET_INFORMATION`. That Task Manager's Efficiency mode is exactly EcoQoS + low priority **[unverified]** | API **[verified: SetProcessInformation doc]** |
| A3.5 | Open file location | Explorer with the exe selected (`SHOpenFolderAndSelectItems`) | **[unverified API detail]** |
| A3.7 | Freeze / Resume any app group (adopted 2026-11-04 from B, A-10) | `IProcessControl.Freeze(tree)` / `Thaw(tree)`, the same members B3.7 uses; `FrozenRegistry` records intent before the first suspend and `RecoverFrozen` thaws on agent start; per process: Toolhelp thread snapshot filtered by PID → `OpenThread(THREAD_SUSPEND_RESUME)` → `SuspendThread` (`Wow64SuspendThread` for WOW64 threads from a 64-bit caller), repeated until a pass finds no new threads; no process-level right, no undocumented `NtSuspendProcess`. Thread right **[verified: SuspendThread doc, via B]**; whether non-admin `OpenThread` succeeds on own-user threads and is refused on elevated ones **[unverified; B's W1 freeze probe, B3 acceptance #12, answers A3.7 too]** | design (shared with B) |
| A3.6 | Open Task Manager | launches `taskmgr`; pre-selecting the process has no documented way **[unverified]**, so it opens on Processes | — |

**Guards (former A4)**
- **Own-user, same-integrity only.** Windows' default no-write-up policy blocks a standard (medium-integrity) process from writing to a higher-integrity one **[verified: mandatory-integrity-control]**, so elevated and SYSTEM processes refuse End / priority from Tasktor; Tasktor checks first and greys the row instead of failing on click.
- **Protected processes** refuse `PROCESS_TERMINATE`-class rights entirely; only `PROCESS_QUERY_LIMITED_INFORMATION` is allowed **[verified: process-security-and-access-rights]** → shown with reason "Protected by Windows".
- **Critical-process denylist** (ship read-only): csrss, wininit, winlogon, smss, services, lsass, dwm, explorer (End disabled; "Restart Explorer" may come later), and `tasktor-core` itself.
- **Two-step confirm** for End and End tree: the first click turns the button into "End Chrome (29)?" for 5 s; the second click acts. Rules (A6) can never end a process without that confirmation.
- **Fresh check at action time:** PID, creation time and exe re-read before the call; on a mismatch it aborts with "Process changed, refresh".
- **Freeze (A3.7) guards:** the critical-process denylist also disables Freeze, and so do the Widgets / shell hosts Tasktor's own viewers need. A frozen row shows "Frozen 12 m" with **Resume** first in its menu. A frozen GUI app looks "Not responding" to Windows, so F must not act on it. Rules never freeze anything on their own in A (I4 may later, with its own guard).
- Every action is logged in the local log (H6): time, exe name, action, result. No command lines are logged.

**Sampling:** no sampling of its own; acts on the latest A2 snapshot plus a re-check.

**Edge cases:** a group with mixed admin / non-admin members (ends what it can, reports "ended 27 of 29, 2 run as admin"); a process that ignores `WM_CLOSE`; apps that restart themselves (Teams, OneDrive): report "restarted" if the same exe reappears within 5 s; ending a terminal's group also ends its jobs (the confirm lists the child count).

**Acceptance**
1. A test process of your own user ends after two clicks, never after one (UI test).
2. An elevated test process shows disabled with the "runs as admin" reason and no call is attempted (manual, reference PC).
3. A denylisted exe shows End disabled (unit test).
4. Below-normal then restore returns the original priority class (test).
5. Efficiency mode sets EcoQoS: read back with `GetProcessInformation` (test) and Task Manager shows the leaf icon (manual).
6. PID reuse: a process replaced between snapshot and click is not ended (test with a fake snapshot).
7. Mixed group: the partial-result message counts are correct (test).
8. Freeze then Resume on a test app group: every process is suspended, then resumed exactly once; killing the agent while frozen and restarting it thaws the group (test with a fake `IProcessControl`, then manual on the reference PC).

## A5. Disk guard

**Reference-PC probe (2026-11-04, non-admin):** C: 32.2 GB free of 475.6 GB (7%), down from 44 GB at Frame time (2026-11-03). Two fixed volumes reported, one with a letter **[verified: local probe]**.

**What the user sees**
| Surface | Content |
|---|---|
| S | Nothing unless a volume is below threshold: then the verdict line becomes "C: 32 GB free (7%)" |
| M / L | One bar per fixed volume with a letter: free GB and %, colour by threshold; L adds a "Biggest folders" button |
| Toast | "C: is low: 32 GB free (7%)" with buttons Biggest folders / Open Storage settings / Snooze 1 day |

**Data sources (non-admin)**
- Free / total per volume: `GetDiskFreeSpaceEx` on fixed volumes (`GetDriveType` = fixed) **[unverified API detail, check in A0; the `Get-Volume` equivalent probed above]**.
- Settings links: `ms-settings:storagesense` (Storage page) and `ms-settings:storagepolicies` (Storage Sense) **[verified: learn.microsoft.com launch-settings]**.
- Biggest folders: a user-mode directory walk (`FindFirstFileEx`, basic info, large fetch) summing **allocated size on disk**, not file length, so OneDrive cloud-only placeholders count as ~0 and are never downloaded by the scan (attributes are read, contents never opened) **[unverified: hydration-free reads and placeholder attribute flags, check in W1]**. Fast MFT-based scanning (as in WizTree) needs admin **[unverified]**, so it is out under "never elevate".
- Folders the user cannot read (System Volume Information, WindowsApps, other users' profiles) are counted and listed as "not readable", not errors.

**Sampling:** free space is part of the 60 s tick (D-A1), which is also what the threshold rule reads. The folder scan runs only on request: BelowNormal priority, cancellable, streams progress, results cached with a timestamp ("scanned 12 min ago"), never automatic.

**Defaults and settings (editable rule in `rules.json`)**
- `when volume.free < max(10%, 15 GB) for 5 min then toast`; re-alert only after another 5 GB is lost or after 24 h; Snooze 1 day.
- Per-volume override and per-volume off switch. Removable and network drives are off by default.
- **Filling faster than usual (adopted 2026-11-04 from B, A-11).** A second rule uses the shared adaptive pattern: `KindStats` keyed per volume learns the normal fill rate (GB/h over the tick's free-space series, sleep spans excluded), and `LearnedThreshold` fires when the rate exceeds 1.5× the learned normal for 30 min **and** the volume would hit the low threshold within 24 h at that rate. Fallback until learned: ≥ 5 GB/h for 30 min. The ramp, clamp and `GuardrailSet` checks follow B's D-B5/D-B9 numbers; the toast says "learned" or "default" for its threshold. Numbers only, keyed by a hash of the volume id; forgettable (D-B10). No extra sampling: it reads the 60 s tick. Defaults (5 GB/h, 1.5×, 30 min) are proposals to check in W1 **[unverified]**.

**Actions:** Open in Explorer (folder), Open Storage settings, Open Storage Sense, rescan. **Tasktor never deletes anything** in A5; cleanup belongs to Windows or the user.

**Edge cases:** volumes without a letter (recovery partitions) hidden; BitLocker-locked volumes show "locked"; a scan under 91% full with ~1M files must not starve the PC (BelowNormal + I/O priority low **[unverified API]**); junctions and symlinks are not followed (no double counting, no loops); a drive removed mid-scan ends the scan cleanly.

**Acceptance**
1. Free GB and % match Explorer's This PC view within 0.1 GB (manual).
2. Rule fires once when a fake volume crosses the threshold, not again until 5 GB more are lost or 24 h pass (test with a fake clock).
3. Scanning a folder that holds OneDrive cloud-only files downloads nothing: OneDrive status unchanged and network bytes flat (manual, reference PC).
4. A junction loop fixture terminates and counts each file once (unit test).
5. Cancel stops the scan within 1 s (test).
6. Fill-rate rule: with a recorded series, the default fires at ≥ 5 GB/h before 5 learned runs, the learned threshold takes over after them, and the toast labels which one fired (test, fake clock).
7. A full C: scan on the reference PC finishes and its total is within 5% of used space minus the "not readable" share (manual; time recorded as a baseline).

## A6. Pressure alerts (RAM, commit; CPU / disk proposed)
**Status:** settled. CPU and disk saturation rules ship **off** by default (busy CPU is often intended: builds, ML jobs); RAM and commit rules on.

**Why commit as well as RAM:** on the reference PC, 29.5 of 31.6 GB commit limit was in use at the A1 probe (93%) **[verified: local probe]**. When commit reaches the limit, allocations fail and apps crash, even with some physical RAM free, so commit is the sharper warning **[unverified as stated; standard Windows memory-manager behaviour to cite in W1]**.

**Detection (tiered, so it stays inside the D-A3 budget)**
1. **Watch cheaply:** wait on Windows' `LowMemoryResourceNotification` event **[verified: CreateMemoryResourceNotification doc]**; read RAM %, commit % and CPU / disk totals on the 60 s tick.
2. **Escalate:** if the event fires or any total crosses its warn level, sample totals and per-process data every 5 s for up to 2 min.
3. **Confirm:** alert only if the condition holds for the rule's duration; otherwise drop back to the tick and free per-process buffers.
The offender comes from the escalation window: the A2 group with the most private memory and its growth in that window ("Chrome 3.1 GB, +600 MB in 1 min").

**Default rules (`rules.json`, editable)**
| Rule | Condition | Default |
|---|---|---|
| RAM pressure | Windows low-memory event, or RAM used ≥ 90% for 30 s | on |
| Commit pressure | commit ≥ 90% of limit for 30 s | on |
| CPU saturation  | CPU ≥ 90% for 2 min | off |
| Disk saturation  | disk active ≥ 95% for 2 min | off |
Re-arm only after the value falls 10 points below the threshold for 2 min; at most one alert per offender per 30 min.

**Learned growth per app (adopted 2026-11-04 from B, A-11).** The offender's growth is judged against that app's own normal. `KindStats` keyed by a hash of the `AppIdentity` app id learns typical growth per escalation window, and `LearnedThreshold` marks growth above 1.5× that normal as "faster than usual for Chrome". Fallback until learned: the fixed +500 MB/min **[proposal]**. It only adds wording and the offender choice; the pressure rules above still decide whether to alert, so learning never silences a RAM or commit alert. Data comes only from escalation windows (no extra sampling, D-A3). Numbers only, forgettable (D-B10); `GuardrailSet` falls back to the fixed value if the learned one misfires.

**What the user sees**
| Surface | Content |
|---|---|
| Toast | "Memory is low: Chrome is using 3.1 GB (+600 MB in 1 min)". Buttons: **Close Chrome** (graceful A3.0; the app may still ask to save), **Show** (flyout on that group), **Snooze 1 h**. No End button in a toast: hard end stays two-step in the flyout (A3 guards) |
| S | The verdict line (A2.7) turns critical colour with the same text |
| M / L / Flyout | The offender row is highlighted; the hour sparkline (A1.6) gets a spike marker |


**Acceptance**
1. A test program allocating until commit ≥ 90% raises exactly one toast naming it within 45 s (manual, reference PC).
2. A short 10 s spike above the threshold raises nothing (test with recorded series).
3. After an alert, no repeat for the same offender within 30 min unless snoozed state is cleared (test, fake clock).
4. Escalated 5 s sampling stops and buffers are freed within 2 min of the condition clearing (test).
5. Close on the toast sends a graceful close to the right group (test with a dummy window app).
6. Learned growth: an app whose recorded normal is +800 MB/min is not called "faster than usual" at +700 MB/min; an app with a +50 MB/min normal is at +200 MB/min; a RAM alert still fires in both cases (test with recorded series).

## A7. Dev ports (Module)
**Status:** settled. A7.6 "open to network" badge accepted (report only).

**Reference-PC probe (2026-11-04, non-admin):** 29 TCP listeners, every one with its owning PID. Most belong to Windows (System, svchost, lsass, wininit, services, spoolsv; ports 135/139/445/5040/7680 and 49664–49673). User-owned ones include ollama on 127.0.0.1:11434, pythonw on 127.0.0.1:7535, StudioMCP on 127.0.0.1:13469, and SpotifyLauncher on **0.0.0.0**:49674, which is reachable from the network **[verified: local probe]**.

**Data source:** `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_LISTENER`, called for both `AF_INET` and `AF_INET6`; returns the listening endpoints with their owning PID **[verified: GetExtendedTcpTable doc]**. No admin was needed on the probe **[verified: local probe]**; the doc itself states no privilege requirement. UDP is left out of v1 (no "listening" state to show).

**What the user sees**
| Surface | Content |
|---|---|
| S | None |
| M / L | Dev ports card: up to 5 rows of `port · app · address` ("3000 · node · localhost") |
| Flyout | Full list with toggles "show Windows ports" and "show ephemeral (49152+)"; per row: port, address, app group (A2), PID, start time, command line on hover |

**Default filter (editable rule):** show listeners owned by your own user's processes; hide SYSTEM / service-owned ones and the dynamic range 49152–65535 unless owned by a user process. On the reference PC that leaves 4–5 rows of the 29.

**Actions**
- **Open in browser** → `http://localhost:<port>` (shown for every port; it is just a link).
- **Copy address**.
- **Close / End owner**: A3 rules apply (graceful first, two-step End, never elevate).
-  **A7.6 Exposed badge**: a listener bound to `0.0.0.0` / `::` (all interfaces) instead of loopback gets an "open to network" badge with a one-line explanation. Report only; Tasktor does not touch the firewall.
- **A7.5 Port-conflict hint**: entering a port in the flyout's search answers "3000 is taken by node (PID …, started 14:02)" or "free".

**Sampling:** only while the Dev ports card or flyout is open, every 5 s (ports change rarely); never on the tick. Cost: one call per address family per sample.

**Module behaviour:** off by default (Tier Module); turning it on adds the card, nothing else.

**Edge cases:** PID 4 (System) owns kernel listeners such as SMB; port owned by an exited process (row disappears on the next sample); WSL / Docker / Hyper-V forwarded ports show the forwarding service, not the Linux process **[unverified]**; the same port on IPv4 and IPv6 is merged into one row with both addresses.

**Acceptance**
1. Starting `python -m http.server 8765` shows a row "8765 · python · all interfaces (exposed)" within 5 s; stopping it removes the row (manual).
2. The default filter on the reference-PC snapshot keeps exactly the user-owned listeners (unit test on the recorded probe).
3. IPv4 + IPv6 listeners on the same port collapse into one row (unit test).
4. End owner on a test listener follows A3's two-step confirm (UI test).
5. Port-conflict search returns the right owner or "free" (test).

## A8. Battery (laptops only)
**Status:** settled. Moved to **W1, Core** (laptops only).

**Reference-PC probe (2026-11-04, non-admin):** one battery "Primary", 100%, on AC, Balanced power scheme. Runtime fields return "unknown" sentinels on AC (`BatteryLifeTime` −1; WMI `EstimatedRunTime` 71582788) **[verified: local probe]**, matching the doc ("–1 if remaining seconds are unknown or if the device is connected to AC power") **[verified: SYSTEM_POWER_STATUS doc]**.

**Data source (non-admin, event-driven)**
- `GetSystemPowerStatus` → `SYSTEM_POWER_STATUS`: `ACLineStatus` (0 off / 1 on / 255 unknown), `BatteryFlag` (8 charging, **128 no system battery**, 255 unknown), `BatteryLifePercent` (255 unknown), `SystemStatusFlag` (battery saver), `BatteryLifeTime` (s, −1 unknown) **[verified: SYSTEM_POWER_STATUS doc]**.
- Changes arrive as events, not polling: `RegisterPowerSettingNotification` with `GUID_POWER_SAVING_STATUS` **[verified: same doc]**; AC/DC source and percent-remaining GUIDs **[unverified names, check in A0]**. Fits D-A3: zero wake-ups for battery between events.
- Settings link: `ms-settings:batterysaver` (only on devices with a battery) **[verified: launch-settings doc]**.

**What the user sees**
| Surface | Content |
|---|---|
| S | Unchanged on AC. On battery, a small battery glyph + % beside the three numbers |
| M / L | A battery row when on battery: %, time left, battery-saver state; hidden on AC unless the user pins it |
| Flyout | %, charging / discharging, time left (or "calculating"), battery saver on/off, power mode; link to Battery settings |
| Toast | A8.2 only (below). Tasktor does **not** repeat Windows' own low-battery warnings |

**A8.2 Unplugged-under-load rule (`rules.json`, on by default on laptops)**
- `when power source → battery and (CPU ≥ 50% over the last 2 min or GPU ≥ 50% or a B-section job is running) then toast`.
- Toast: "Unplugged while python is busy: ~1 h 10 m left at this rate". Buttons: **Efficiency mode** for that app (A3.4), **Battery settings**, **Dismiss**. Uses the tick history (D-A1) for the 2-min look-back, so no extra sampling.
- Boundary: A8.2 only warns. Automatically throttling jobs and pausing refits on battery is I4 (power guard).

**Edge cases:** desktops (`BatteryFlag` 128): the whole feature and its rule are hidden; UPS-backed desktops may report a battery **[unverified]**; two batteries (some ThinkPads) → aggregate percent **[unverified source: WinRT `AggregateBattery`]**; unknown values (255 / −1) shown as "—", never as 255% or −1 min; plugging back in withdraws the toast.

**Acceptance**
1. On a PC without a battery, no battery UI and no A8.2 rule appear (unit test with `BatteryFlag` 128).
2. Unplugging the reference laptop during a CPU-heavy test process raises exactly one toast within 10 s; unplugging at idle raises none (manual).
3. Percent and charging state match the Windows battery flyout (manual).
4. Unknown sentinels never render as raw numbers (unit test).
5. With no viewer open, battery handling causes no periodic wake-ups (trace / counter test, D-A3).

## A9. Per-process GPU % → folded into A1.3 / A2.3
**Status:** folded. Spec lives in A1 (GPU row) and A2 (GPU column); coverage note in the subfeature outline.
- Probe 2026-11-03 on the reference PC, non-admin: `\GPU Engine(*)\Utilization Percentage` returns 507 instances named `pid_<pid>_luid_<adapter>_phys_<n>_eng_<n>_engtype_<type>`. So per-process, per-adapter, per-engine data exists on Iris Xe without admin **[verified: local probe]**. Microsoft's own reference page for this counter set was not found; a Microsoft Q&A moderator answer gives the same enumerate-then-filter-by-PID method **[community, learn.microsoft.com/answers/questions/5641645]**. Aggregation that matches Task Manager (busiest engine type per adapter) comes from a third-party crate **[unverified]**.

## A10. CPU temperature → cut
**Status:** cut. A Pack may add it.
- Probe 2026-11-03 on the reference PC, non-admin: `MSAcpi_ThermalZoneTemperature` (root/wmi) → **Access denied** **[verified: local probe]**. No Microsoft reference page found. Community sources say it is firmware/OEM dependent, often absent, and is an ACPI zone, not the CPU die sensor **[unverified]**.

## Open questions
Decided enough to build; these are checks to run in the named phase, not decisions.
| # | Question | Resolved by |
|---|---|---|
| OQ1 | Which CPU counter Task Manager's tabs use today (Time vs Utility); A1 acceptance compares against it either way | W1 manual check |
| OQ2 | Per-process snapshot: ~600 `OpenProcess` calls vs one `NtQuerySystemInformation` call; cost and stability | A0 measurement |
| OQ3 | Which memory field matches Task Manager's "Memory" column (private working set) | W1 |
| OQ4 | Agent memory / CPU budgets (D-A3) hold on the A0 skeleton | A0 |
| OQ5 | Folder scan reads attributes without hydrating OneDrive placeholders; low I/O priority API | W1 |
| OQ6 | Efficiency mode = EcoQoS + low priority exactly as Task Manager does | W1 |
| OQ7 | Power-setting GUID names for AC/DC and percent; multi-battery aggregation | A0 |
| OQ10 | GPU row hidden on VMs / Basic Display Adapter / unsupported installs | W1 |
| OQ11 | WSL / Docker forwarded ports show the forwarding service, not the Linux process | T2 |
| OQ12 | ~~Stack~~ **Decided 2026-11-04: C# everywhere (Native AOT agent) + Python**, after a passing spike; details in the catalog list. Remaining: COM *server* (widget provider) under Native AOT | W0 |

## Changes for the catalog
_(for the parent session to merge into `01-catalog.md`, `00-frame.md` and `WORKSTREAMS.md`)_
**Renamed / reshaped**
- A1: add "+ 60-min history" (A1.6, from proposed A12) and editable colour thresholds (A1.7). GPU per adapter included (from A9).
- A2: add GPU column (from A9), "System / services" bucket (A2.6), and the "why is it slow" verdict line (A2.7, from proposed A11). Default grouping: tool processes (node, python, cmd …) count under the launching app.
- A3: rename to "Per-process actions and guards"; absorbs A4. Adds **Close (graceful) first** (A3.0) and **Efficiency mode** (A3.4). Tasktor **never elevates**.
- A6: rename to "Pressure alerts": RAM + commit (on); CPU and disk saturation rules ship **off**.
- A7: add the "open to network" badge (A7.6) and port-conflict lookup (A7.5).

**Cut / folded**
- A4 → folded into A3.
- A9 → folded into A1.3 / A2.3; status "limited" → "planned" (WDDM 2.0 is a Win11 minimum requirement).
- A10 → cut from the public app (Pack may add it).

**Re-tiered / re-phased**
- A8 Battery: "later" → **W1, Core (laptops only)**. Boundary: A8.2 warns only; throttling stays in I4.

**Cross-document changes**
- `00-frame.md` C1.1 / H5: "sample only while viewed" now has one exception: **system totals every 60 s, always, last hour in memory** (D-A1).
- H5: replace "RAM budget set at A0" with the D-A3 budgets: idle ≤ 15 MB private / ≤ 0.1% CPU; viewer open ≤ 30 MB / ≤ 1%; folder scan ≤ 60 MB at BelowNormal. Add a self-budget rule and leak tests.
- C1.4 Alerts: RAM pressure waits on Windows' `LowMemoryResourceNotification` instead of polling.
- `WORKSTREAMS.md` WS1/WS2: A's objects (`Scheduler`/`Demand`, `Platform` traits, `Sampler`, `RingBuffer`, `ProcessTable`, `AppIdentity`/`Grouper`, `RuleEngine`, `ProcessController` + `Guard` chain, `Notifier`, `SnapshotPublisher`) become a **shared core library** that sections B, C, E, H and I compose (D-A4). WS6 (learning) takes app ids from `AppIdentity`.
- **Project-wide design rule:** D-A4's principles apply to every section, not just A: encapsulated objects behind interfaces, composition over inheritance, OS access only through platform adapters (fakeable), push events over polling, minimum time/space with no loss of accuracy, a complexity table per hot path. Proposed home: `00-frame.md` → a new "Engineering principles" block, and each WS spec.
- **D-A5 data-first design, project-wide:** design from the data. Four shapes (series, table, tree, event) each get one set of reusable classes; interfaces only at real seams, seven in total, with no per-resource interfaces. Four runtime assemblies (Contracts / Core / Windows / Hosts) plus test projects that hold all fakes. Built-in features and plugins share `ICardProvider`.
- **Shared, not A-owned:** everything in Contracts and Core is project infrastructure owned by WS0/WS1/WS2, named by data shape, never by an A feature. Section A contributes only its feature classes (A1 SystemTotals, A2 Apps, …) and the Windows sources it needed first. Rules for every section spec: (1) reuse the shape classes and the seven interfaces before adding anything; (2) a new shape or interface is a project-level change, reviewed against all sections, not added inside one section; (3) each section lists the sources and features it adds against the D-A5 mapping table. Needs: a WS0 update (Contracts assembly + plugin SDK) and a `WORKSTREAMS.md` note that WS1/WS2 own the shared core.
- Stack question (OQ12), as weighed before the spike. the maintainer first asked about Java for the OOP part. Options:
  1. Keep Rust for `tasktor-core` and apply the OO rules through traits + composition. Holds the D-A3 budgets (15 MB idle). OOP is a design discipline, not a language feature.
  2. Java for the whole agent via GraalVM native-image + the FFM API for Win32. A typical JVM's idle footprint is well above 15 MB; native-image narrows the gap **[unverified: measure in a spike before deciding]**. Adds a 4th language (Rust/C#/Python → Java/C#/Python if it replaces Rust).
  3. Java inside the Rust agent (an embedded JVM): rejected, because it loads a full JVM into the always-on process and breaks D-A3.
  4. Java where it is not always-on: plugins (C4 is language-neutral) or tools.
  Also on the table: C#, already in the stack for the widget, is classic OO and supports ahead-of-time native compilation; the Frame already names "all C#" as its fallback **[footprint unverified]**.
- **OQ12 weighed (2026-11-04); the maintainer leans "C# overall" and wants to be hands-on:**
  | Criterion | Rust agent + C# widget (Frame) | **C# everywhere (agent, widget, flyout)** | Java agent + C# widget |
  |---|---|---|---|
  | Idle RAM vs the 15 MB budget | best | Native AOT gives "smaller memory footprints" with no runtime install **[verified: learn.microsoft.com native-aot]**; whether it fits 15 MB is **[unverified: spike]** | weakest; native-image needed **[unverified]** |
  | Classic OO (classes, interfaces) | traits + composition | yes | yes |
  | the maintainer learns hands-on | lower | **high (the user's stated goal)** | medium (already knows Java) |
  | Languages in the repo | 3 (Rust, C#, Python) | **2 (C#, Python)** | 3, Rust swapped for Java |
  | Agent ↔ widget seam | JSON schema across languages | **one shared C# domain library compiled into both**; IPC stays, contract types are shared | JSON across languages |
  | Win32 / WinAppSDK | windows-rs | P/Invoke source generation; WinAppSDK 1.6+ supports Native AOT **[verified: Windows developer blog, 2024-09-04]** | FFM API; no WinAppSDK |
  | Risks | — | Native AOT has "no built-in COM" on Windows **[verified: native-aot doc]**, and the widget provider and UI Automation are COM, so they need source-generated COM **[unverified: spike]**; loses the agent-desktop Rust crates (Frame C1.2 candidate); Native AOT publishing needs VS "Desktop development with C++" **[verified: native-aot doc]**, which the user's VS lacks (Frame), so it is a machine change needing the user's OK | 4th toolchain, worst footprint |
  **Recommendation:** C# everywhere, **gated by a short spike**: a Native AOT console agent running the D-A1 tick + one PDH query + one source-generated COM call, measured idle on the reference PC. Pass (≤ 15 MB private, ≤ 0.1% CPU) → C# replaces Rust in the Frame. Fail → he either accepts a revised budget or the agent stays Rust, with C# for widget/flyout and the shared contracts. Python stays for learning (C2) either way.
  **Spike result (2026-11-04, reference PC): PASS → C# everywhere adopted.**
  The spike was a .NET 8 console agent. It ran the D-A1 tick at 60 s: one PDH collect (CPU %, available MB) into a fixed 60-slot ring buffer, plus one source-generated COM call (UI Automation `GetRootElement`, S_OK every tick), and used `LibraryImport` P/Invoke, workstation non-concurrent GC and invariant globalization. Each build ran idle for 192 s (3 ticks) side by side; the machine was at 33–41% CPU from other work.
  | Build | Private working set | Private commit | Working set (incl. shared DLLs) | CPU avg (of 12 logical) | Threads | Exe |
  |---|---|---|---|---|---|---|
  | **Native AOT** | **6.1 MB** | **8.1 MB** | 19.8 MB | **0.0020%** | 5 | 2.0 MB, no runtime needed |
  | JIT (framework-dependent) | 8.9 MB | 10.7 MB | 34.3 MB | 0.0014% | 7 | needs .NET runtime |
  Budget (D-A3): ≤ 15 MB private, ≤ 0.1% CPU → Native AOT uses ~half the memory budget and ~1/50 of the CPU budget **[verified: measured]**. Caveats: the real agent adds the process table, pipe server and rule engine, so headroom is ~7 MB, re-measured at A0 (OQ4). Not covered: a COM *server* (the widget provider, `GeneratedComClass`); that check moves to W0. Build gotcha: Native AOT publish calls `vswhere.exe` without a path, so the VS Installer folder must be on PATH (a CI and docs note).
