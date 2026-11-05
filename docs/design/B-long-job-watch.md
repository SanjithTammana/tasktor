# Tasktor — B. Long-job watch (feature spec)

**Status:** **signed**. Rebuilt around adaptive policies with fixed fallbacks and guardrails (LDOS pattern). D-B1–D-B11 decided; B1–B6 accepted. Open items are W1 probes or belong to other sections (see Consolidated at signing).
**Sessions:** 2026-11-03 (stopped before A was signed); 2026-11-04 full draft built the way A was. Parent catalog: `../01-catalog.md` (rows B1–B5). Frame: `../00-frame.md` (C1.3 job watch, C1.4 alerts, C2.6 refit). Workstreams: WS2 Sensing (process rows, sources), WS4 Rules + actions (owns B1–B5).
**Fixed inputs (signed, not relitigated):** `A-system-monitor.md`: D-A1 tick, D-A3 budgets (idle ≤ 15 MB private / ≤ 0.1% CPU), D-A4/D-A5 shared core (four shapes, seven interfaces), D-A6 trees + blocks/hints, A2 (tool processes count under the launching app; individual jobs are B's), A3 guards (Close first, two-step End, never elevate). Stack: C# everywhere, agent as Native AOT, Python for learning; Rust is out.
**Reference low end:** i7-1355U (10 cores / 12 threads), 12 GB RAM, Iris Xe, one monitor.

Conventions: **[verified: source]** = checked against Microsoft docs or a local probe this session; **[unverified]** = still a claim, checked in the named phase (A0 = skeleton measurement, W0 = toolchain spike, W1 = first build on the user's PC, L2 = learning phase). Sizes: **S / M / L** = Widgets-board small / medium / large card; **Flyout** = own window (G2). **** = proposed this session, not yet accepted.

**Why this section exists (the failure it prevents):** a long CPU job with no visible progress looks the same as a hang, so it gets killed and loses its work; an uncapped job at normal priority makes the desktop unusable (the user's own note `long-local-compute-jobs`: a 15-minute embedding run with no output and 6.3 cores at Normal priority was killed as "unsafe and heavily taxing"). Job watch answers three questions at a glance: *is it still working, how far along is it, is it hurting the machine.*

**Design stance.** B learns what *normal* looks like for each kind of job on this PC and judges a run against that, instead of against one number for everyone. Every fixed number in this spec is kept, but only as the **known-safe fallback**: it decides on day one, for a kind of job never seen before, and whenever a **guardrail** stops trusting what was learned. This is the structure LDOS's Guardrails paper proposes for learned OS policies (see Prior art and the Adaptive core section).

## Reference-PC probes (2026-11-04, non-admin, read-only)
- **Who would be a job right now.** 20 processes match the default watch list: node 13, python 6, pythonw 1. Every command line was readable through WMI `Win32_Process.CommandLine` without admin. Parents: codex, claude, ChatGPT, cmd, py, svchost **[verified: local probe]**.
- **The old "≥ 30 s CPU in total" gate fails both ways.** The highest total was 25.5 s, by a Codex `node` runtime that is 15 h old and almost idle, so it will cross 30 s on drift alone and turn into a false job. Short `python` scripts 2–7 min old had used 5–6 s, which is real work but under the floor. MCP servers (`node … mcp_server`, `./mcp/server.mjs`) used 0.3–0.5 s each **[verified: local probe]**.
- **A 30 s window.** Over 30 s every match used ≤ 0.001 cores and wrote 0 KB/s. So at this moment the windowed gate (D-B1) admits **nothing**, which is correct: no long job was running **[verified: local probe]**.
- **Default excludes:** the command-line patterns (`mcp`, `--stdio`, `languageserver`, `pylance`) matched 10 of 20 **[verified: local probe]**. The rest (Codex runtimes, `cmd`-launched node) are idle, and the windowed gate keeps them out without a list entry.
- **Priority today:** 19 of 20 run at **Normal**; the one BelowNormal process is a background `pythonw` service **[verified: local probe]**. Low priority is not the default anywhere, which is the case B3 exists for.

## Prior art
| Tool | What it does | What B takes from it |
|---|---|---|
| Process Lasso ProBalance (Windows) | Lowers a background process to Below Normal only when **both** the system and that process exceed CPU thresholds for a duration; restores "as soon as the condition passes"; never touches the foreground process; Efficiency mode optional and off by default as "more heavy-handed". Lowering priority changes precedence, not CPU entitlement, so an uncontended Below Normal process runs at full speed **[source: bitsum.com ProBalance + CPU throttling docs; default numbers not published]** | **Folded:** hogging needs system contention too (B1.7); auto-lower is "restrain while contended, restore after" (B3.4) |
| fish `done` plugin | Notifies when a command ran > 5 s **and** its terminal is not in the foreground; regex excludes; failed commands get critical urgency **[source: github.com/franciscolourenco/done]** | **Folded:** finished toast only if the job's window is not in front (B2.5) |
| systemd `sd_notify` | `WATCHDOG=1` keep-alive "in regular intervals"; `STATUS=` free text (may carry a percentage); `EXTEND_TIMEOUT_USEC=` to announce slowness; `WATCHDOG=trigger` to self-report an error **[source: freedesktop.org sd_notify(3)]** | **Folded:** the writer may declare its own heartbeat (`heartbeat_s`, B4); stuck = missed its own heartbeat |
| OSC 9;4 (ConEmu progress) | `ESC ] 9 ; 4 ; state ; pct BEL`; Windows Terminal ≥ 1.6 shows a ring on the tab and a bar on the taskbar; states 0 hidden, 1 normal, 2 error, 3 indeterminate, 4 warning **[verified: learn.microsoft.com Windows Terminal progress-bar sequences]** | **Folded:** B4 states use these names; the Python helper writes the file **and** prints OSC 9;4. Tasktor cannot read the sequence (only the terminal sees the stream; design inference) |
| Xfennec `progress` (Linux/macOS) | Infers progress with no cooperation: finds `cp`/`tar`/`gzip`…, reads open files' seek positions vs size **[source: github.com/Xfennec/progress]** | Not folded: the Windows equivalent (another process's open handles) is undocumented → OQ-B9, later or a Pack |
| **LDOS** (NSF Expedition, UT Austin + Penn, UW–Madison, UIUC; lead PI Aditya Akella) | Argues OS policies are fixed heuristics that "bake in assumptions" and break when workloads change; treats policy design as data-driven. Names the costs: learned policies are data-hungry, wrong decisions are hard to detect, inference/feature/training overhead can erase the gain **[source: sigops.org 2026 LDOS blog; cs.utexas.edu 2024 news]** | **Folded:** the whole section's stance (D-B9); keep learning cheap (P5) and data-light, because one PC's history per job kind is small |
| **LDOS Guardrails** (Saxena, Chen, …, Rossbach, Swift, HotOS 2025) | A guardrail = a **property** + a **corrective action**. Properties over a model's inputs (P1 in-distribution), outputs (P2 robustness, P3 bounds) and system behaviour (P4 better than the heuristic, P5 overhead justified, P6 fairness/liveness). Actions: REPORT, REPLACE (fall back to the existing policy, which "can start making decisions immediately"), RETRAIN, DEPRIORITIZE. Triggers (timer / function) are decoupled from rules; a shared feature store (SAVE/LOAD) holds aggregates. Example: LinnOS switched off when its false-submit rate > 5%. Open problems: guardrails feeding back into each other (oscillation), periodic checking **[verified: paper text, rcs.uwaterloo.ca/hotos25/hotos25-156.pdf]** | **Folded:** every adaptive B policy has a fallback and a guardrail set mapped to P1–P6 and the four actions (Adaptive core); hysteresis against oscillation; guardrail checks event-driven (on job end / alarm), not periodic |
| **LAKE** (ASPLOS 2023) | In-kernel ML with feature collection APIs and a policy that falls back to cheaper (CPU) paths when ML is predicted not to pay off; "the benefits of ML … are subsystem-, workload- and hardware-dependent" **[source: par.nsf.gov LAKE full text, via search summary]** | **Folded:** learning must pay for itself on the reference PC; B's learning is streaming statistics in C# Core, no model file, no Python |
| Windows "Not responding" (`IsHungAppWindow`) | A window is hung if it has not called `PeekMessage` within 5 s ("subject to change"); "not intended for general use" **[verified: IsHungAppWindow doc]** | Confirms console jobs have no OS hang signal, so B2's evidence-of-work design stands |

## Verified platform facts (used across B1–B6)
| Fact | Evidence |
|---|---|
| Toolhelp's `PROCESSENTRY32W` has PID, parent PID, thread count, base priority and exe name, but **no creation time**. A names-only scan is one call; creation time (PID-reuse guard) needs a handle + `GetProcessTimes` for the processes we keep | **[verified: PROCESSENTRY32W doc]** |
| `SetPriorityClass` needs `PROCESS_SET_INFORMATION` | **[verified: SetPriorityClass doc]** |
| Background mode (`PROCESS_MODE_BACKGROUND_BEGIN`, which also lowers I/O and memory priority) "can be specified only if hProcess is a handle to the current process", so Tasktor cannot put *another* process in background mode | **[verified: same page]** |
| Children inherit low priority: "If the calling process is IDLE_PRIORITY_CLASS or BELOW_NORMAL_PRIORITY_CLASS, the new process will inherit this class." Workers spawned *after* a lowering inherit it; running workers do not | **[verified: Scheduling Priorities]** |
| EcoQoS on another process: `SetProcessInformation(ProcessPowerThrottling, EXECUTION_SPEED)` with `PROCESS_SET_INFORMATION` | **[verified: SetProcessInformation doc, via A3.4]** |
| `GetProcessIoCounters` (read / write / other bytes and op counts) needs only `PROCESS_QUERY_LIMITED_INFORMATION`. Whether network or console writes count is not stated | **[verified: GetProcessIoCounters, IO_COUNTERS]**; network/console **[unverified, W1]** |
| PIDs are reused; identity = (PID, creation time) | **[verified: Win32_Process]** |
| `GetExitCodeProcess` needs `PROCESS_QUERY_LIMITED_INFORMATION`; returns `STILL_ACTIVE` (259) while running, so a real exit code of 259 is ambiguous unless read after the exit wait fires | **[verified: GetExitCodeProcess doc]** |
| Exit as a push event: `RegisterWaitForSingleObject` accepts process handles, which need `SYNCHRONIZE`; a process stays signalled, so use `WT_EXECUTEONLYONCE`; `UnregisterWait` is still required; closing the handle while the wait is pending is undefined | **[verified: RegisterWaitForSingleObject doc]** |
| Command line: WMI `Win32_Process.CommandLine` works non-admin for own processes. `NtQueryInformationProcess` is "internal … subject to change" and does not list a command-line class | **[verified: local probe; NtQueryInformationProcess doc]** |
| Folder watch: `ReadDirectoryChangesW` (handle with `FILE_LIST_DIRECTORY`, async via overlapped I/O); on buffer overflow it returns 0 bytes or `ERROR_NOTIFY_ENUM_DIR` and the caller must re-enumerate; last-write changes are seen "only when the file is written to the disk" (cache flush) | **[verified: ReadDirectoryChangesW doc]** |
| Job Objects (for the deferred core cap, B3.5): assigning needs `PROCESS_SET_QUOTA` + `PROCESS_TERMINATE`; since Windows 8 a process already in a job can join a new empty job (nesting); children join the job by default; a hard CPU cap is `CpuRate` = percent × 100 of the whole machine | **[verified: AssignProcessToJobObject, JOBOBJECT_CPU_RATE_CONTROL_INFORMATION docs]** |
| Freeze: `SuspendThread` needs a thread handle with `THREAD_SUSPEND_RESUME`; it increments a per-thread suspend count (`ResumeThread` decrements; capped at `MAXIMUM_SUSPEND_COUNT`). "Primarily designed for use by debuggers"; deadlock risk is when the **caller** then tries to take a lock a suspended thread owns. A 64-bit caller suspends WOW64 threads with `Wow64SuspendThread`. No documented whole-process suspend exists (`NtSuspendProcess` is undocumented, not used) | **[verified: SuspendThread doc]** |
| Free RAM while frozen: `EmptyWorkingSet` "removes as many pages as possible from the working set"; needs `PROCESS_QUERY_LIMITED_INFORMATION` + `PROCESS_SET_QUOTA`. Pages return from the pagefile on demand after resume. GPU memory is not affected **[GPU part unverified, W1 probe]** | **[verified: EmptyWorkingSet doc]** |
| Toasts (Windows App SDK): buttons can act without opening a window; activation needs a COM activator in the MSIX manifest; not supported for elevated apps (`Show` fails silently). Portable build is A's OQ8 (G/H) | **[verified: app-notifications quickstart, 2026-09-10]** |

## Subfeature outline (draft, 2026-11-04)
**B1. Job rows** (accepted 2026-11-04; ✓ = accepted  item)
- B1.1 Watch list (exe names) + exclude patterns (command line), editable rules
- B1.2 Windowed gate (D-B1, decided): age ≥ 60 s and ≥ 30 s CPU within the last 2 min; sticky once a job (replaces "≥ 30 s CPU in total")
- B1.3 ✓ Bypasses: a B4 progress file names the process, or the user pins it ("Watch as job" from A2's flyout)
- B1.4 Job trees: watch-list workers fold into the topmost watch-list ancestor; per-worker CPU on expand
- B1.5 Row: label, age, CPU now **in cores**, CPU time, priority badge, progress, status dot
- B1.6 Exit as a push event (D-B4) with exit code; finished rows kept 10 min (done / failed / ended)
- B1.7 ✓ "Hogging" badge: a Normal-priority job using ≥ half the logical CPUs **while system CPU ≥ 80%**, both for 2 min (ProBalance's two-gate test), gets a badge and a one-click Lower
- B1.8 ✓ **Learned admission** (fallback: D-B1 gate): kinds of process that never become jobs (MCP servers, editor runtimes) are probed less often; kinds that usually become jobs appear as "starting" at 60 s without waiting for the CPU window
- B1.9 ✓ **"Watch as job?" suggestion** for exes not on the list that behaved like jobs while a viewer was open (no extra sampling)
- B1.10 ✓ **Learned ETA** for jobs without a progress file: "usually ~1 h 10 m (12 runs)"

**B2. Silent-job flag + toast** (accepted 2026-11-04)
- B2.1 ✓ Evidence of work: CPU, disk I/O, progress-file heartbeat, log-file growth (if B4 names a log)
- B2.2 ✓ **Adaptive silent** (D-B5): quiet for longer than this kind of job is normally quiet, learned per kind; fallback 10 min; clamped 2–60 min; tiered GPU check before flagging
- B2.3 ✓ Stuck-busy: CPU busy but the progress file missed its heartbeat (2 × `heartbeat_s`, else 10 min) (B4 jobs only)
- B2.4 Silent toast: Show, Open log, **It's fine** (snoozes this run and teaches the policy it was a false alarm); never End
- B2.5 ✓ Finished toast (done / failed) for jobs that ran ≥ 10 min, **only if the job's window is not in front** (fish `done` pattern); on by default
- B2.6 Hysteresis: back to "working" on renewed activity; one toast per job per silence episode
- B2.7 ✓ **Longer than usual**: a run past 1.5 × this kind's usual (p90) duration gets a note on its row; no toast by default
- B2.8 ✓ Learned muting: a kind whose finished toast you dismiss 5 times in a row is offered "Stop notifying for train.py?" (suggestion, never silent)

**B3. Priority and stop actions (via A3)** (accepted 2026-11-04, incl. rename)
- B3.1 Lower = Below normal on the **whole job tree** (root + running workers); new workers inherit
- B3.2 Efficiency mode on the tree (A3.4)
- B3.3 Restore (original class per process remembered)
- B3.4 ✓ Auto-restrain rule (ProBalance-style): lower a hogging job while the system is contended, restore when it clears; never the foreground app; **off** by default. ✓ Learned suggestion: a kind you lowered by hand in 3 of its last 5 runs gets "Always restrain train.py when the PC is busy?" (one click creates a rule for that kind only)
- B3.6 End job tree (A3.2, two-step); ✓ graceful Ctrl+C for console jobs **[unverified, W1 probe]**
- B3.7 ✓ **Pause job**: **Freeze** (suspend every thread of the job tree, any job; optional "free RAM") and **Polite pause** (jobs using the B4 helper stop at a safe point). Resume continues where it stopped. Checkpoint-and-stop stays B5 / job-owned. **[W1 probe with a real PyTorch job]**

**B4. Progress files (`jobs/<id>.json`)** (accepted 2026-11-04)
- B4.1 Folder `%LOCALAPPDATA%\Tasktor\jobs\` (H's layout; MSIX virtualization caveat)
- B4.2 Schema v1 (below): id, pid, label, pct or done/total, stage, msg, eta, log, state (OSC 9;4 names), heartbeat_s, updated
- B4.3 Writer rules: atomic write (temp + rename), ≤ 4 KB, ≤ 1 write/s honoured by debounce
- B4.4 Push watch (`ReadDirectoryChangesW`), re-enumerate on overflow
- B4.5 Untrusted input: bounded parse, text-only rendering, log path opened only on click, never executed
- B4.6 Lifecycle: `done` / `error` states; files of gone processes cleaned after 24 h
- B4.7 ✓ Writers: a stdlib-only Python helper (writes the file **and** prints OSC 9;4 so the terminal tab and taskbar show the same progress) + `tasktor job` CLI (D); public spec in the repo docs

**B5. Tasktor's own long tasks as jobs** (accepted 2026-11-04; widened from "own refits")
- B5.2 Shows stage, %, and the limits it runs under (BelowNormal + EcoQoS, 25% CPU, 1.5 GB)
- B5.3 Cancel; ✓ Pause = cancel + resume from the last checkpoint stage (C8 checkpoints)
- B5.4 ✓ The A5 folder scan uses the same row (it is a long internal task too)

**B6. Job kinds: what Tasktor learned** (accepted 2026-11-04; new row)
- B6.1 Per kind: runs seen, learned vs fallback for each policy and why ("silent after 14 min: longest normal quiet gap 9 min over 12 runs"), false alarms
- B6.2 Forget one kind / forget all; global "Learn from my jobs" switch (off = every policy uses its fallback)
- B6.3 Guardrail log: every REPLACE / relearn with its reason

**Catalog-shape proposals**
- **B3 renamed** "Priority and stop actions": it also hosts End-tree for jobs (A3.2), and its core cap is deferred. **B3.7 Pause job** added.
- **B5 widened** to "Tasktor's own long tasks" (refit + folder scan) and stays **L2** for refits; the folder-scan row ships with A5 in W1.
- **"Finished" toast folded into B2** (B2.5), not a new row.
- **B1.7 hogging badge** is new; it is the visible half of "is it hurting the machine".
- **B6 added** (job kinds and learned state): the user-facing half of the guardrails, Core / W1, because adaptive decisions must be explainable from day one.
- No cuts. Nothing moves tier: B1–B4 stay Core / W1. The learned layers ship in W1 too: they are streaming statistics, not the C-section learning pipeline (L-phases).

## Cross-cutting decisions
- **D-B1 Job gate.** A watch-list process becomes a job when age ≥ 60 s **and** it used ≥ 30 s of CPU within the last 2 min (≥ 25% of one core on average). Once a job, it stays a job until it exits. Bypasses: a progress file names it, or the user pins it. Why not "≥ 30 s in total": the probe shows that floor admits a 15 h idle helper and rejects 5-minute scripts doing real work.
- **D-B2 Job watch is always on.** A names-only process scan rides the D-A1 60 s tick, viewer or not, because the silent toast must work with no viewer open. Per-process handles are opened **only for watch-list matches**. This is a narrow amendment to A2's "per-process data only while viewed", limited to the watch list.
- **D-B3 Two views of the same processes.** A2 keeps counting jobs under the launching app (signed); B lists the job tree. B never shows machine totals, so nothing is counted twice in any sum.
- **D-B4 Exits are pushed.** Each job holds one handle (`PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`) with a one-shot wait; the exit code comes from that handle. No polling for exits.
- **D-B5 Silent is judged against this job's own normal — DECIDED.** Evidence of work is several signals (CPU, disk I/O, progress heartbeat, log growth); a job is quiet when all are quiet. It is **silent** when the current quiet gap exceeds what is normal for its kind: about 1.5 × the longest quiet gap seen in its recent successful runs, clamped to 2–60 min. **Trust ramp:** below 5 good runs the 10-min fallback decides alone; from 5 to 20 good runs the threshold moves linearly from the fallback to the learned value; at 20 (the full history window) it is fully learned. The fallback also decides for out-of-distribution runs and after a guardrail REPLACE. A tiered GPU read runs before flagging. Full policy in the Adaptive core section.
- **D-B9 Adaptive by design — DECIDED.** Each B decision that rests on a number is an **adaptive policy**: a learned estimate per job kind, the fixed number as the fallback, and guardrails (LDOS P1–P6 → REPORT / REPLACE / RELEARN) that choose which one acts. Each policy has a **trust ramp**: the fallback decides while a kind has little history, and the learned estimate takes over gradually as good runs accumulate (silent: 5 → 20 runs). Learning is local streaming statistics in C# Core (no model files, no Python, no network). Every adaptive decision shows its basis and whether it came from "learned" or "default".
- **D-B10 Learned data stores no names — DECIDED.** A job kind is a keyed hash of exe + script name; only numbers are stored per kind. The readable name exists only in memory while a job of that kind is running. "Forget" per kind and for all; global off switch. Command lines are still never stored (C's privacy rule).
- **D-B11 Type design — DECIDED (the maintainer 2026-11-04, incl. RecoverFrozen, RequestPause (now `SendControl`, A-C4) and the one-logic-thread rule).** Assemblies, records, class responsibilities, the job state machine, interface members, threading, memory layout and test seams: see Architecture → D-B11. `JobWatch` becomes a thin façade over seven focused Core classes.
- **D-B7 CPU in cores — DECIDED.** Rows say "6.3 cores", not "52%". It is the number that predicts a frozen desktop, and it does not change meaning between a 4-core and a 16-core PC.

### D-B8 Efficiency: B must fit inside D-A3 — DECIDED
**Where B could spend memory or CPU, and the design that bounds it** (sizes are estimates until A0 measures them **[unverified]**)
| Part | Risk | Design |
|---|---|---|
| Finding candidates | a per-process walk every tick | one Toolhelp snapshot per 60 s tick, names only (no handle per process); string compare against an interned watch list. O(n) once a minute |
| Candidate handles | ~600 opens | opened only for watch-list matches (20 on the user's PC today); kept open while they are candidates, closed when they leave |
| Command lines | WMI is heavy (COM + WmiPrvSE) | read **once per (PID, creation time)**, batched into one WMI query per tick for all new candidates, cached; never re-read. Zero WMI calls on ticks with no new candidates |
| Per-job series | history per job | `RingBuffer` of 60 slots × 10 s (the 10-min sparkline) × 3 numbers (CPU, I/O, progress age) ≈ 1.5 KB per job; fixed. Silence is not read from the ring (the learned threshold can reach 60 min): each job keeps `lastEvidenceAt` and the run's longest quiet gap, two numbers, O(1) per sample |
| Rows kept after exit | grows if many short jobs | finished rows capped at 10 min and 20 rows (`TopN` by end time) |
| Progress folder | polling a folder | `ReadDirectoryChangesW` push, one fixed 4 KB buffer; a changed file is re-read at most once per second (debounce); files > 4 KB rejected unread |
| Exit detection | polling | thread-pool waits, one per job; no wake-ups until an exit |
| GPU check (B2) | the GPU Engine PDH set has ~500 instances | opened only for the pre-flag check, filtered to one PID, closed immediately |
| Viewer open | 1–2 s sampling | only job rows (k ≈ 1–10) at 1–2 s through a `DemandLease`; the scan stays at 60 s |
| Learned state (D-B9) | grows with every kind ever seen | ≤ 256 kinds × ~300 B ≈ 80 KB, LRU; updated only at job end / alarm label; written at most once a minute; learned admission *reduces* candidate probing for idle kinds |

**Budgets (inside D-A3, not added to it)**
| State | B's share of agent private memory | B's share of agent CPU |
|---|---|---|
| No jobs, no viewer | ≤ 0.5 MB (watch list, candidate cache, learned state ≤ 0.1 MB) | one snapshot per minute: ≤ 0.01% |
| 1–10 jobs, no viewer | ≤ 1 MB more | 10 s job reads: ≤ 0.02% |
| Viewer open | within A's 30 MB / 1% | 1–2 s job reads |

**Acceptance**
1. With no job running and no viewer, B adds at most one process snapshot per minute and zero WMI calls after warm-up (counter test, fake clock).
2. 8 h on the reference PC with jobs starting and ending every few minutes: agent private memory does not grow by > 1 MB (manual + logged).
3. A script rewriting its progress file 100 times a second costs the agent < 1% CPU (debounce test).
4. After a job exits, its handle and wait are released within 1 s (handle-count test).

## Adaptive core (D-B9, decided)
**Shape of every adaptive policy** (the diagram shown in session 2026-11-04): job signals → per-kind statistics → a **learned** proposal and a **fallback** proposal → a **guardrail** picks one, clamps it and logs why → decision → **outcome labels** (exit code, your button) → statistics updated.

**Job kind (signature).** `kind = HMAC-SHA256(install_key, lower(exe) + "|" + script)` truncated to 16 bytes, where `script` is the basename of the first non-flag argument (`train.py`), or empty. `install_key` is 32 random bytes in `state\`, protected with DPAPI for the user **[unverified API choice, W1]**. Two runs of `python train.py --fold 3` and `--fold 4` are one kind; `python embed.py` is another.

**Per-kind statistics (`KindStats`, ~300 B each, at most 256 kinds, least-recently-used dropped)**
| Field | Holds | Feeds |
|---|---|---|
| `runs`, `goodRuns` | counts; good = exit 0, not ended by the user or Tasktor, not flagged true-silent | all |
| `quietMax[20]` | longest quiet gap of each of the last 20 good runs (seconds), GPU-busy and sleep time excluded | silent (B2.2) |
| `duration[20]` | wall time of the last 20 good runs | ETA (B1.10), longer than usual (B2.7) |
| `coresMedian[20]` | each good run's median cores | in-distribution check (P1) |
| `becameJob`, `stayedIdle` | how often a candidate of this kind passed / never passed the D-B1 gate | learned admission (B1.8) |
| `alarms`, `falseAlarms` | silent toasts and the ones labelled false | decision quality (P4) |
| `shadowFalseAlarms` | false alarms the fallback **would** have raised on the same runs | decision quality (P4) |
| `lowered[5]`, `finishedDismissed` | your manual Lower per run; finished toasts dismissed in a row | suggestions (B3.4, B2.8) |
| `mode`, `cleanRunsSinceReplace` | learned / fallback, and the hysteresis counter | guardrails |
Fixed-size rings, so every quantile is **exact** over the window, O(1) to update, O(20) to read; no streaming approximation needed at this size.

**Policies**
| Policy | Learned estimate | Fallback (fixed) | Labels it learns from |
|---|---|---|---|
| Silent (B2.2) | `T = clamp(w·L + (1−w)·600 s, 120 s, 3600 s)`, `L = 1.5 × max(quietMax)` (p90 once n ≥ 10), trust ramp `w = clamp((n − 5) / 15, 0, 1)`, n = good runs: pure fallback below 5, fully learned at 20 | 10 min | job resumed and exited 0 after a flag → false alarm; **It's fine** → false alarm; you ended it, or it failed → true alarm |
| Stuck-busy (B2.3) | the writer's `heartbeat_s` (declared, not learned) | 10 min | same as silent |
| Admission (B1.8) | `stayedIdle ≥ 5 and becameJob = 0` → probe every 10 min instead of every tick; `becameJob ≥ 2 of last 3` → show as "starting" at 60 s | D-B1 gate every tick | whether the candidate then passed the gate |
| ETA (B1.10) | median of `duration`, shown with its run count; remaining = median − elapsed | none shown (n < 3) | each good run's duration |
| Longer than usual (B2.7) | elapsed > 1.5 × p90(`duration`) | off (n < 5) | each good run's duration |
| Restrain suggestion (B3.4) | `lowered` in ≥ 3 of last 5 runs | no suggestion | your manual Lower |
| Finished muting (B2.8) | `finishedDismissed ≥ 5` | always toast | dismiss vs Open folder / Open log |
Not adaptive, on purpose: the hogging badge thresholds (they describe the machine, not the job), End (always a person's two-step choice), and anything that would act on a process without a rule you approved.

**Guardrails** (LDOS P1–P6 mapped onto B; each is a rule in the rules file, so a user can tighten it)
| Property | B check | Action |
|---|---|---|
| P1 in-distribution | this run's median cores outside `[0.5 × p10, 2 × p90]` of `coresMedian`, or n < 5 good runs | use the fallback **for this run**; REPORT |
| P2 robustness | the learned threshold changed by > 2× between consecutive runs | keep the previous value until 2 more runs agree; REPORT |
| P3 bounds | silent threshold within 2–60 min; ETA only with n ≥ 3; learned admission can only *show* a row early or *probe* less, never hide a job that passes the gate | clamp |
| P4 decision quality | over the kind's last 10 alarms, `falseAlarms > 30%` **or** more than `shadowFalseAlarms` (the learned policy must beat the fallback it replaces) | REPLACE with the fallback for that kind until 5 clean runs (hysteresis against oscillation); REPORT |
| P5 overhead | all learned state ≤ 100 KB; updates only at job end and on alarms | over budget → drop least-recently-used kinds; REPORT |
| P6 liveness | a restrained job (B3.4) whose cores fall to ~0 while system CPU < 50% for 2 min | restore its priority; REPORT |
Actions map onto LDOS's: REPORT = guardrail log (B6.3, H6); REPLACE = `mode = fallback`; RETRAIN = **relearn** (clear that kind's rings, keep counts); DEPRIORITIZE is B3.4 itself and is never triggered by a guardrail. Guardrails run when a job ends or an alarm is labelled, not on a timer, which is the event-driven checking the paper lists as future work.

**Cold start and the off switch.** With no history, or with "Learn from my jobs" off, every policy uses its fallback, and B behaves exactly as the fixed-rule spec. So adaptive B is never worse on day one, and the fallbacks are what the acceptance tests for each feature pin down.

**Explainability (every adaptive decision).** The row, toast or B6 view states the basis and the source: "silent 14 min: longer than any quiet gap in its last 12 runs (max 9 min) · learned" or "silent 10 min · default (new job)".

**Persistence.** `state\job-kinds.json` (machine-written, `schema_version`), rewritten atomically at most once a minute and only after a job ends; loaded lazily. Excluded from `tasktor settings export` unless `--include-learned`.

**Acceptance (adaptive core)**
1. With learning off, every B acceptance test passes unchanged (fallbacks = fixed spec) (test).
2. A kind whose good runs pause up to 12 min per epoch: a new kind (n < 5) is flagged at 10 min; after 20 good runs a 12-min pause is not flagged (threshold 18 min); at 12 runs the threshold sits between the two (≈ 13.7 min) (test, fake clock).
3. A kind that is normally never quiet > 30 s is flagged after 2 min of silence (the clamp) once it has 20 good runs, and at 10 min while it has fewer than 5 (test).
4. Injected false alarms above 30% switch that kind to the fallback; it returns to learned only after 5 clean runs (test).
5. A run with cores far outside the kind's history uses the fallback and logs a P1 report (test).
6. `job-kinds.json` contains no exe or script name in any form a byte search can find (test on fixtures).
7. 256 kinds of state stay ≤ 100 KB in memory; a 257th evicts the least recently used (test).

## Architecture (builds on D-A4 / D-A5 / D-A6; no parallel classes)

### B's data by shape, mapped onto the shared core
| Shape | Data in B | Key | Shared class it uses |
|---|---|---|---|
| **Table** | candidates and jobs | (PID, creation time) | `KeyedTable<ProcKey, JobRow>`; `Diff()` gives new / gone |
| **Table** | progress rows (files, refits, folder scan) | job id | `KeyedTable<JobId, ProgressRow>` |
| **Series** | per job: CPU cores, I/O bytes/s, progress age | job key + metric | `RateCounter` (CPU time → cores, bytes → bytes/s), `RingBuffer` (60 × 10 s) |
| **Tree** | job tree (root + workers) | child → parent | `TreeRollup` with anchor = topmost watch-list ancestor (stops at the first non-watch-list parent) |
| **Table** | learned per-kind statistics | kind hash | `KeyedTable<KindId, KindStats>` (bounded, LRU); rings are `RingBuffer<float>` of 20 |
| **Event** | job new / silent / resumed / hogging / done / failed; process exit; progress-file changed | type + time | `EventChannel<JobEvent>` (feeds H10's `job.done` and `process … silent_for`), `Cooldown`, `HoldFor` |

### Slot map: where B plugs in (A's D-A6 slots)
| Slot | B uses it for |
|---|---|
| New data | `ProcessExitEvents : IEventSource<ProcessExit>`; `JobProgressSource : ITableSource<JobId, ProgressRow>` (files); `WorkerProgressSource : ITableSource<JobId, ProgressRow>` (refit stdout, B5) |
| New behaviour | `JobWatch : Feature, ICardProvider` |
| New decision | rules in the rules file: silent, stuck-busy, hogging, finished, auto-restrain (off); **a new condition class `LearnedThreshold`** (A's slot map allows new condition classes) and the guardrail rules over it |
| New look | existing blocks only: `Table`, `Progress`, `Badge`, `Sparkline`, `Actions`, `Alert` |
| Outside code | progress files: any script or tool becomes a job without a plugin |

### New classes, and why each is a real gap
| New | Why the shared core does not already cover it |
|---|---|
| `JobWatch` (Feature) | every section adds its own Feature; this is B's |
| `JobGate` (policy) | classifies processes into jobs. It reuses `AppIdentity`'s ordered-rule matcher (rules resolved once per distinct exe + command line) and adds one windowed rate check. It is a classifier, not a rule with actions, so it is not a `RuleEngine` rule |
| `ProcessExitEvents` (Windows source) | no existing source pushes exits or gives exit codes; a new implementation of an existing interface, not a new interface |
| `JobProgressSource`, `WorkerProgressSource` (sources) | new data; both emit the same `ProgressRow`, so B5 costs no extra types |
| `ProgressRow`, `JobRow` (Contracts records) | new data shapes in the existing table pattern |
| `DemandLease` **column set** (change to an existing class) | lets B ask `ProcessTableSource` for names/parent only at 60 s without A2's per-process opens. A parameter, not a type; a project-level change (listed in the catalog changes) |
| `KindStats` + `KindId` (Core) | per-kind learned statistics (Adaptive core); nothing in the shared core stores outcome-labelled history per key |
| `LearnedThreshold` (condition class) | a `RuleEngine` condition whose threshold comes from `KindStats` with a fallback value, bounds and a `source` (learned / default) for explanations. Lets silent and longer-than-usual stay ordinary rules |
| Guardrail rules + two rule actions `use_fallback`, `relearn` | guardrails are plain rules over policy metrics (false-alarm rate, P1 distance, threshold change); only the two actions are new, so they are an H10 grammar addition, not a new engine |
No new interface. No new block kind.

### Where B sits
```
 Windows ──► Sources (Tasktor.Windows) ──────────────► Core (shared) ──────────────► JobWatch ──► Snapshot ──► Viewers
  Toolhelp     ProcessTableSource [names, 60 s] ───┐                                    (Feature,     jobs[]       S/M/L card,
  OpenProcess  (+ cmdline via WMI, once per job)   ├─► KeyedTable<ProcKey,JobRow> ─►    ICardProvider)             flyout, tray,
  WMI          ProcessExitEvents [push] ───────────┤   JobGate ─► TreeRollup             │                         CLI
  Rd.Dir.Chg.  JobProgressSource [push] ───────────┤   RateCounter + RingBuffer ─────────┤
  worker stdout WorkerProgressSource [push] ───────┘   KeyedTable<JobId,ProgressRow> ────┤
                                                        RuleEngine (silent, hogging, ────► INotifier (toasts)
                                                        finished) + HoldFor + Cooldown    IProcessControl + GuardChain (A3)
```
Frame mapping: sources + gate = C1.3; rules + toasts = C1.4; refit row = C2.6; viewers = C3. Scheduler: one lease on the 60 s tick (always), one 10 s lease while jobs exist, one 1–2 s lease while a viewer is open.

### OO model
| Object | Responsibility | Used by B | Reused by |
|---|---|---|---|
| `Scheduler` + `DemandLease` (shared, + column set) | runs the scan at 60 s, job reads at 10 s, viewer reads at 1–2 s; frees buffers at zero leases | B1 | A2, C, I |
| `ProcessTableSource` (shared) | snapshot; with the names-only column set, no per-process handle | B1 | A2, C (start/stop diff) |
| `KeyedTable` + `Diff` (shared) | candidates, jobs, progress rows; new / gone | B1, B4, B5 | A2, A7 |
| `JobGate` | watch list + excludes + windowed CPU gate + pins + progress-file bypass; sticky | B1 | I2 (busy-process classes) |
| `TreeRollup` (shared) | folds workers into the job root; per-worker values kept | B1.4 | A2, A5 |
| `RateCounter`, `RingBuffer` (shared) | CPU cores, I/O rate, 10-min sparkline (silence is a running counter in `ActivityMonitor`, D-B11) | B1, B2 | A1, A2 |
| `ProcessExitEvents` | one-shot waits, exit codes, releases handles | B1.6 | C (episode ends), H10 `job.done`, A3 "already exited" |
| `JobProgressSource` / `WorkerProgressSource` | parse and validate progress (file / stdout) into `ProgressRow` | B4, B5 | D (CLI `tasktor job`), plugins |
| `RuleEngine` + `HoldFor` + `Cooldown` (shared) | silent, stuck-busy, hogging, finished, auto-restrain | B2, B3.4 | A5, A6, I |
| `INotifier` (shared) | toasts with buttons and cooldowns | B2 | A5, A6, E |
| `JobWatch` | owns the job table, emits `JobEvent`s, builds the card (blocks + hints) | all of B | G (card), D (snapshot `jobs[]`), A8.2 ("a job is running") |
| `KindStats` | per-kind rings and counters; exact quantiles over 20 runs; LRU-bounded; persisted | B1.8–B1.10, B2, B3.4, B6 | A6 (an app's normal memory growth), I2/I13 (normal duration of recurring tasks) |
| `LearnedThreshold` | learned value + fallback + clamp + source, as a `RuleEngine` condition | B2.2, B2.7 | any rule that should adapt per key (A5 disk-growth rate, A6) |

### Data sources (one place)
| Data | Adapter | Windows source | Admin | Push or poll |
|---|---|---|---|---|
| Exe name, PID, parent, thread count | `ProcessTableSource` (names-only set) | `CreateToolhelp32Snapshot` / `PROCESSENTRY32W` | no | poll, 60 s tick |
| Creation time, CPU time | same, per candidate | `GetProcessTimes` with `PROCESS_QUERY_LIMITED_INFORMATION` **[unverified right for GetProcessTimes; A2 flagged it too, A0]** | no | poll, 60 s (candidates) / 10 s (jobs) |
| I/O bytes | same, per job | `GetProcessIoCounters` | no | poll, 10 s |
| Command line | same, once per job | WMI `Win32_Process.CommandLine` | no (own user) | once |
| Priority class | same, per job | `GetPriorityClass` | no | on detection + after actions |
| Exit + code | `ProcessExitEvents` | `RegisterWaitForSingleObject` + `GetExitCodeProcess` | no | **push** |
| Progress files | `JobProgressSource` | `ReadDirectoryChangesW` on `jobs\` | no | **push** |
| Refit progress | `WorkerProgressSource` | the worker's stdout pipe (agent is the parent) | no | **push** |
| Learned state | `KindStats` store | `state\job-kinds.json` (Tasktor's own file) | no | load once, write ≤ 1/min after job end |
| GPU use for one PID (pre-flag check) | A's `PdhMetricSource` | `\GPU Engine(pid_<pid>*)\Utilization Percentage` | no | once per check |

### Time and space per operation (n ≈ 600 processes, c = watch-list candidates ≈ 20, k = jobs ≈ 1–10, w = workers per job, f = progress files, r = rules)
| Operation | Naive | Design | Exact? |
|---|---|---|---|
| Find candidates | open every process each tick, O(n) handles | names-only snapshot, O(n) string compares, zero handles; interned names → O(1) compare | yes |
| Gate check | keep per-candidate history | `RateCounter`: two CPU-time reads 2 min apart, O(1) per candidate; O(c) per tick | yes (CPU time is exact) |
| Command line | WMI per candidate per tick | once per (PID, creation time), one batched query per tick, cached → O(new) | yes |
| Fold workers | walk to root per worker each sample, O(k·w·depth) | `TreeRollup` anchors memoised per process, O(k·w) | yes |
| Per-job rates | — | O(1) per process per read; O(k·w) per 10 s | yes |
| Silent check | re-scan a window each read, O(window) | `ActivityMonitor`'s `lastEvidenceAt` vs the cached `LearnedThreshold`, O(1) | yes |
| Exit detection | poll every job each second | one wait per job, O(1) at exit, zero work otherwise | yes |
| Progress files | re-read the folder every tick, O(f) | push notifications, re-read only changed files, debounced 1 s; overflow → one O(f) enumeration | yes |
| Rows to viewers | serialise all candidates | cards get `TopN` jobs (S 1, M 3, L 6); the flyout pulls the full table | yes |
| Learned threshold | keep every quiet gap ever seen, sort per check | 20-slot ring per kind; max / p90 over 20, O(20) per job end; O(1) per check (threshold cached per run) | yes (exact over the window) |
| Guardrail evaluation | re-check all kinds on a timer | only the kind whose job ended or whose alarm was labelled, O(1) | yes |
| Space | — | O(c) candidate cache + O(k·60) ring slots + O(f) progress rows, f capped at 64 files + ≤ 256 × ~300 B learned state | yes |

### D-B11 Type design: structures, classes, interfaces — DECIDED

**1. Where every B type lives (A's four assemblies; no new assembly)**
| Assembly | B types |
|---|---|
| `Tasktor.Contracts` (records, no logic, no Windows) | `ProcKey`, `JobId`, `KindId`, `JobRow`, `WorkerRow`, `ProgressRow`, `ProgressState`, `JobStatus`, `JobEvent` + `JobEventKind`, `PauseMode`, `ControlRequest`, `LearnedValue`, `ValueSource`, the progress-file schema v1, the `jobs[]` snapshot block |
| `Tasktor.Core` (pure logic, Linux CI) | `JobWatch` (Feature façade), `JobTracker`, `JobGate`, `ActivityMonitor`, `ProgressJoiner`, `KindLearner`, `KindStats`, `KeyedHasher` (shared), `LearnedThreshold`, `GuardrailSet<TSubject>` (shared), `PauseCoordinator`, `JobCardBuilder` |
| `Tasktor.Windows` (Win32 behind the 7 interfaces) | `ProcessExitEvents : IEventSource<ProcessExit>`, `JobProgressSource : ITableSource<JobId, ProgressRow>`, `WorkerProgressSource : ITableSource<JobId, ProgressRow>`, `ControlFileWriter`, the Freeze / Thaw / TrimMemory half of `Win32ProcessControl` + its `FrozenRegistry`, `InstallKey` (DPAPI) |
| `Tasktor.Agent` (composition root) | wiring, the leases, persistence timing for `KindStats` (bytes in / bytes out, written by the host) |
| `Tasktor.Core.Tests` | fakes for every seam below, a recorded-job replay set, the fake clock |

**2. Value types and records (Contracts)**
| Type | Fields (type) | Size | Notes |
|---|---|---|---|
| `ProcKey` (readonly struct) | `Pid` (uint), `CreatedTicks` (long) | 12 B | process identity; every lookup and every action re-checks it (PID reuse) |
| `JobId` (readonly struct) | `Root` (ProcKey) for watched processes, or `Name` (interned string id) for progress-file and internal jobs | 16 B | one id per job tree; progress rows join by `pid` → `ProcKey` |
| `KindId` (readonly struct) | 16 bytes (truncated HMAC-SHA256) | 16 B | D-B10; never reversible to a name |
| `JobStatus` (enum, byte) | `Starting`, `Working`, `WorkingGpu`, `Quiet`, `Silent`, `StuckBusy`, `Paused`, `Done`, `Failed`, `Ended` | 1 B | the state machine below; one value per job |
| `PauseMode` (enum, byte) | `None`, `Polite`, `Frozen`, `FrozenTrimmed` | 1 B | B3.7 |
| `JobRow` (record) | `Id`, `Kind` (KindId), `Label` (string id, live only), `Status`, `Pause`, `StartedUtc`, `CpuTime` (TimeSpan), `CoresNow` (float), `IoBytesPerSec` (float), `Priority` (enum), `Restrained` (bool), `Hogging` (bool), `QuietSince` (long ticks or 0), `LongestQuietGap` (TimeSpan), `PausedTotal` (TimeSpan), `ExitCode` (int?), `Workers` (count) | ~120 B | the row the card, snapshot and rules read; built from tracker state, not stored twice |
| `WorkerRow` (record) | `Key`, `Exe` (string id), `CoresNow`, `CpuTime`, `Priority` | ~40 B | only materialised when a viewer expands a job (flyout) |
| `ProgressRow` (record) | `Id`, `Pid` (uint?), `Label`, `Pct` (float?), `Done` / `Total` (long?), `Unit`, `Stage`, `Msg`, `EtaS` (int?), `LogPath`, `State` (ProgressState), `HeartbeatS` (int?), `Paused` (bool), `UpdatedUtc`, `Source` (file / worker / internal) | ≤ 4 KB source, ~200 B parsed | strings length-capped at parse (label 64, stage 32, msg 160, path 260) |
| `ProgressState` (enum) | `Running`, `Indeterminate`, `Warning`, `Error`, `Done` | 1 B | OSC 9;4 names (D-B6) |
| `JobEvent` (record) | `Kind` (JobEventKind), `Id`, `AtUtc`, `Status`, `ExitCode` (int?), `Basis` (LearnedValue?) | ~48 B | `JobEventKind` = `Started`, `BecameSilent`, `Resumed`, `StuckBusy`, `Hogging`, `Finished`, `Paused`, `Unpaused`, `GuardrailFired`. Feeds H10 (`job.done`), C (episode ends), toasts |
| `LearnedValue` (record) | `Value` (TimeSpan or float), `Source` (ValueSource), `Runs` (int), `Basis` (short text key + numbers, e.g. `quiet.max`, 9 min, 12) | ~32 B | every adaptive decision carries one, so the toast and B6 can explain it without re-deriving |
| `ValueSource` (enum) | `Default`, `Blended`, `Learned`, `FallbackGuardrail`, `FallbackOutOfRange` | 1 B | the badge text |

**3. Core classes: state, operations, invariants**
`JobWatch` is a thin `Feature` façade so no single class grows into a god object. It composes the parts below and exposes one card and one snapshot block.

| Class | Owns (state) | Operations | Invariants | Cost |
|---|---|---|---|---|
| `JobWatch : Feature, ICardProvider` | the parts below; its leases | `Enable()` / `Disable()` (take or release the 60 s, 10 s and 1–2 s leases); `OnTick(frame)`; `OnEvent(e)`; `BuildCard(size)`; `Snapshot()`; `Act(action, id)` | holds the 10 s lease only while ≥ 1 job exists; the viewer lease only while a viewer asks | O(1) itself |
| `JobGate` | watch list (interned exe ids), exclude patterns (compiled once), pins, per-candidate first CPU-time sample | `Classify(candidate) → NotJob / Candidate / Job`; `Pin(key)`; `Bypass(progressRow)` | sticky: once `Job`, never re-gated for that `ProcKey`; learned admission (B1.8) can only change probe *frequency* or show `Starting`, never hide a job that passes | O(1) per candidate per tick |
| `JobTracker` | `KeyedTable<ProcKey, TrackedProc>` (candidates + jobs), the job-tree anchors (via `TreeRollup`), the state machine per job | `Apply(snapshotDiff)`; `OnExit(ProcessExit)`; `Transition(id, trigger)`; `Rows()`; `Workers(id)` | every transition goes through one method, so each state change emits exactly one `JobEvent`; finished rows kept ≤ 10 min and ≤ 20 rows | O(c + k·w) per tick |
| `ActivityMonitor` | per job: `RateCounter`s (CPU time, I/O bytes), `lastEvidenceAt`, `longestQuietGap`, `pausedTotal`, the 60-slot sparkline `RingBuffer` | `Sample(job, cpuTime, ioBytes, progressUpdated, logGrew, now)`; `QuietFor(job)`; `Exclude(job, span)` (sleep, GPU-busy, paused) | quiet time never includes sleep, GPU-busy or paused spans | O(1) per sample |
| `ProgressJoiner` | `KeyedTable<JobId, ProgressRow>` (≤ 64) | `Upsert(row)`; `Remove(id)`; `ForJob(jobId)`; `Orphans()` (files with no live pid → own rows) | a progress row binds to at most one job; a row whose pid is gone and that is not `done` shows as "writer gone" | O(1) per change |
| `KindStats` | rings `quietMax[20]`, `duration[20]`, `coresMedian[20]` (float), counters (`runs`, `goodRuns`, `becameJob`, `stayedIdle`, `alarms`, `falseAlarms`, `shadowFalseAlarms`, `finishedDismissed`), `lowered` (5-bit mask), `mode`, `cleanRunsSinceReplace`, `lastSeenUtc` | `RecordRun(outcome)`; `RecordAlarm(label)`; `Quantile(ring, q)`; `ToBytes()` / `FromBytes()` | fixed size (~300 B); rings exact over the last 20 good runs | O(1) update, O(20) quantile |
| `KindLearner` | `KeyedTable<KindId, KindStats>` (≤ 256, LRU), the global on/off switch | `Stats(kind)`; `OnJobEnded(job, outcome)`; `OnAlarmLabelled(kind, label)`; `Forget(kind)`; `ForgetAll()`; `Relearn(kind)`; `Serialize()` / `Load(bytes)`; `Dirty` flag | only runs at job end / label time; never on the tick; off → every `LearnedThreshold` returns its fallback | O(1) per event |
| `LearnedThreshold : Condition` (RuleEngine) | policy name, fallback, clamp min/max, ramp (5 → 20 runs), multiplier (1.5), which ring + quantile | `Evaluate(job) → bool`; `Current(kind) → LearnedValue` (cached per run, recomputed at job start and when the kind changes) | result always within clamp; `Source` always set | O(1) per check |
| `PauseCoordinator` | per job: `PauseMode`, `pausedAt`, which mode was used, reminder `Cooldown` | `Pause(id, mode)`; `Resume(id)`; `OnAgentStart(recovered)`; `ReminderDue(now)` | picks Polite when a helper-backed progress row exists, else Freeze; a job is never both restrained and frozen in the UI (frozen wins); quitting resumes all | O(1) |
| `JobCardBuilder` | nothing (stateless) | `Build(rows, size) → Card` (blocks + hints: `Table`, `Progress`, `Badge`, `Sparkline`, `Actions`, `Alert`) | no colours or pixels (D-A6); explanations come from `LearnedValue.Basis` | O(TopN) |

**4. Job state machine (one per job, in `JobTracker.Transition`)**
| From | Trigger | Guard | To | Event emitted |
|---|---|---|---|---|
| (candidate) | gate passes, or bypass / pin | — | `Working` | `Started` |
| (candidate) | learned admission (B1.8) at 60 s | kind usually becomes a job | `Starting` | — |
| `Starting` | gate passes within 5 min | — | `Working` | `Started` |
| `Starting` | 5 min pass without the gate | — | (removed) | — (counted against the admission policy, P4) |
| `Working` | all evidence quiet | — | `Quiet` | — |
| `Working` / `Quiet` | GPU read shows the PID busy | — | `WorkingGpu` | — |
| `Quiet` | quiet ≥ `LearnedThreshold` (silent) | not paused, not sleeping | `Silent` | `BecameSilent` (toast) |
| `Quiet` / `Silent` / `StuckBusy` / `WorkingGpu` | evidence returns | — | `Working` | `Resumed` (only from `Silent` / `StuckBusy`) |
| `Working` | CPU busy, progress heartbeat missed 2 × `heartbeat_s` | has a progress row | `StuckBusy` | `StuckBusy` (toast) |
| any live state | `Pause` | guards pass | `Paused` | `Paused` |
| `Paused` | `Resume`, or agent start recovery | — | `Working` | `Unpaused` (no toast) |
| any live state | process exit, code 0 | — | `Done` | `Finished` |
| any live state | exit, code ≠ 0 | not ended by the user | `Failed` | `Finished` |
| any live state | End by the user / Tasktor | — | `Ended` | `Finished` |
| `Done` / `Failed` / `Ended` | 10 min, or > 20 finished rows | — | (removed) | — |
`Hogging` and `Restrained` are flags on the row, not states, because they are independent of whether the job is working.

**5. Interfaces: what B adds to the seven**
No new interface. Two existing ones gain members (OQ-B16, accepted):
| Interface | Member | In | Out | Failure behaviour |
|---|---|---|---|---|
| `IProcessControl` | `Freeze(tree)` | the job tree's `ProcKey`s | per-process result (frozen / skipped: reason) | records intent in `FrozenRegistry` **before** the first suspend; if any process fails mid-way, the ones already frozen stay frozen and the result says which, so the user can Resume (no silent rollback, no half-state hidden) |
| `IProcessControl` | `Thaw(tree)` | same | per-process result | resumes only threads Tasktor suspended, once each; clears the registry entry last |
| `IProcessControl` | `TrimMemory(key)` | one `ProcKey` | bytes before / after (working set) | best effort; failure is reported, never retried in a loop |
| `IProcessControl` | `RecoverFrozen()` | — | list of thawed jobs | called once by the agent at start |
| `IEventSource<ProcessExit>` | (existing shape) `ProcessExitEvents` implements it | watch requests (`Watch(key)`, `Unwatch(key)`) | `ProcessExit { Key, ExitCode, AtUtc }` | a key that is already gone yields an immediate exit with code read once |
| `ITableSource<JobId, ProgressRow>` | (existing shape) two implementations | — | rows + diff | malformed or oversize input → row rejected and counted, never thrown into Core |

**6. Threading model (proposed for the agent; a project-level decision for H)**
- **One logic thread.** All Core state (tracker, learner, rules) is touched by a single agent loop, so Core has no locks.
- **Sources only enqueue.** Thread-pool callbacks (process exit waits, `ReadDirectoryChangesW` completions, worker stdout reads) post an item onto the loop's `EventChannel`; they never touch Core state.
- **Actions run off the loop.** `Freeze` / `Thaw` / priority changes run on a worker and post their result back as an event, so a slow call never stalls sampling.
- **Persistence.** The host asks `KindLearner.Serialize()` on the loop (a fixed-size byte copy) and writes it on a worker, at most once a minute.

**7. Main flows (sequence, one line per hop)**
1. **Detection (60 s tick):** `Scheduler` → `ProcessTableSource` (names-only) → `JobTracker.Apply(diff)` → `JobGate.Classify` per candidate → `Transition(Started)` → `ProcessExitEvents.Watch(key)` + WMI command line once → `KeyedHasher(kind|)` → `LearnedThreshold.Current(kind)` cached → `GuardrailSet.CheckRunStart` (P1).
2. **Job read (10 s):** `ProcessTableSource` (job rows only: CPU time, I/O) → `ActivityMonitor.Sample` → `RuleEngine` (silent, stuck-busy, hogging) → `Transition` → `INotifier` when a rule fires.
3. **Silent toast and label:** `BecameSilent` → toast with `LearnedValue.Basis` → **It's fine** → `KindLearner.OnAlarmLabelled(false alarm)`; or later exit code / End → true alarm.
4. **Exit:** wait fires on a pool thread → `EventChannel` → `JobTracker.OnExit` → `Transition(Done / Failed)` → `KindLearner.OnJobEnded` (rings, labels, `GuardrailSet.CheckRunEnd`) → `Finished` event → finished toast if the window is not in front → host persists later.
5. **Pause:** user → `JobWatch.Act(Pause)` → `PauseCoordinator.Pause` → `GuardChain` → `IProcessControl.Freeze` or `SendControl(Pause)` → result event → `Transition(Paused)` → `ActivityMonitor.Exclude` starts.

**8. Memory layout (fits D-B8)**
| Item | Per unit | Count | Total |
|---|---|---|---|
| `TrackedProc` (candidate) | ~48 B (`ProcKey`, exe id, first CPU sample, flags) | ≤ 64 candidates | ~3 KB |
| Job state (`JobRow` fields + `ActivityMonitor` counters) | ~160 B | ≤ 10 | ~1.6 KB |
| Sparkline ring (60 × 3 floats) | 720 B | ≤ 10 | ~7 KB |
| Progress rows | ~200 B parsed | ≤ 64 | ~13 KB |
| `KindStats` | ~300 B | ≤ 256 | ~77 KB |
| Frozen registry | ~16 B per thread | a few hundred threads at most | ≤ 8 KB |
| **Total** | | | **≈ 110 KB**, inside the 0.5 MB / 1 MB budgets |

**9. Test seams (all in `Tasktor.Core.Tests`)**
| Seam | Fake | What it lets a test do |
|---|---|---|
| `ITableSource<ProcKey, …>` | scripted snapshots | start / end processes, CPU-time curves, PID reuse |
| `IEventSource<ProcessExit>` | scripted exits | exit codes, exit during a freeze |
| `ITableSource<JobId, ProgressRow>` | scripted rows, malformed bytes | parse limits, heartbeat misses, orphaned files |
| `IProcessControl` | recording fake with injectable failures | partial freeze, access denied, recovery at start |
| `INotifier` | recording fake | toast text, buttons, cooldowns |
| `IClock` | fake clock | 10-min windows, 2-h reminders, 20-run ramps in milliseconds |
| Recorded-job replay | real job traces from the user's PC (numbers only) | the OQ-B14 constant tuning, pre-registered before looking |

**10. Errors and edge handling, by class**
| Where | Case | Behaviour |
|---|---|---|
| `JobTracker` | PID reused between reads | `ProcKey` mismatch → old job ends as `Ended (gone)`, new process starts as a candidate |
| `JobGate` | command line unreadable (access denied) | gate on exe name only; no exclude patterns apply; logged once |
| `ActivityMonitor` | system sleep | sleep span excluded from quiet time (power events from A) |
| `ProgressJoiner` | two files claim one pid | newest `updated` wins; the other shows as its own row |
| `KindLearner` | state file corrupt or a different install key | discard and start empty (all fallbacks); log once |
| `PauseCoordinator` | job exits while frozen | registry entry cleared on exit; nothing to resume |
| `IProcessControl.Freeze` | some threads cannot be suspended | partial result shown; the job is marked `Paused (partial)` with Resume available |

### Reuse summary for sibling sections
What B adds that C–I can use:
- **The progress-file contract (`jobs/<id>.json`)**: any script, CLI (D), plugin (H11) or Pack can show a long task in Tasktor with no plugin code. The schema sits in Contracts next to the snapshot.
- **`ProgressRow` as the generic "long task" row**: refits (C8), the folder scan (A5.3), and later any I-section task use one row and one card.
- **`ProcessExitEvents`**: exit codes and push exits for C (episode boundaries), H10 (`job.done`), and A3 ("already exited", restart detection).
- **`DemandLease` column sets**: any section that needs only process names (C start/stop, I meeting-app detection) gets them without A2's cost.
- **`JobGate`'s windowed-busy pattern**: a reusable classifier for "is this process doing real work", e.g. I3 (opted-in jobs) and I4 (throttle on battery).
- **`KindStats` + `LearnedThreshold` + the guardrail rules**: the adaptive pattern for any per-key "normal" (A6 memory growth per app, A5 disk-fill rate, I13 event timing). This is the strongest reuse candidate in B; adopting it elsewhere is a project-level change.
- **`JobWatch` state for rules**: `jobs.running`, `jobs.silent`, used by A8.2 (unplugged during a job), I3, I4 and I13.

## B1. Job rows
**Status:** accepted.

**What the user sees**
| Surface | Content |
|---|---|
| S | "2 jobs · 1 silent", or "train.py · 42% · 1 h 12 m" when there is one job; hidden text "No jobs" when none |
| M | Top 3 rows (silent first, then hogging, then longest-running): label, progress bar or age, cores now, status dot |
| L | Top 6 rows; per row **Lower** and "…" (Efficiency mode, Open folder, Open log, End…) |
| Flyout | All jobs + finished in the last 10 min. Expand a row: full command line, parent app (A2 group), start time, workers with their own cores and CPU time, a 10-min CPU sparkline, priority per process, progress stage and message |

**Job rules (defaults in the rules file, editable; user entries first)**
- Watch list: `python, pythonw, node, ffmpeg, java, javaw, dotnet, cargo, rustc, Rscript, julia`.
- Excludes (command-line patterns): `--stdio`, `languageserver`, `pylance`, `mcp`.
- Gate (D-B1): age ≥ 60 s and ≥ 30 s CPU in the last 2 min; sticky; bypass by progress file or pin.
- Hogging (B1.7): Normal priority and ≥ 50% of logical CPUs (6 cores on the reference PC) **and** system CPU ≥ 80%, both for 2 min. Without contention a busy job is not hurting anything (Below Normal would not slow it either), so no badge.

**Row fields:** label (script or first file argument from the command line, e.g. `train.py`; else the exe name; a progress file's `label` wins) · age · cores now · CPU time · priority badge (Normal / Below / Eco) · progress bar (B4) · status dot (working / silent / stuck-busy / done / failed).

**Sampling:** 60 s names-only scan (always); candidates' CPU time each tick (every 10 min for kinds learned idle, B1.8); jobs' CPU + I/O every 10 s; jobs at 1–2 s while a viewer is open. Exits pushed.

**Adaptive parts (policies in the Adaptive core)**
- **B1.8 learned admission:** saves probes on known-idle kinds and shows known job kinds as "starting" at 60 s. A row shown early that never passes the gate within 5 min is removed and counted against the policy (P4).
- **B1.9 "Watch as job?":** while a viewer is open A2 already has per-process data; an exe not on the list, started from a shell or terminal, using ≥ 1 core for 2 min, gets a one-time suggestion. Accepting adds it to the watch list. Costs nothing when no viewer is open.
- **B1.10 learned ETA:** for jobs without a progress file: "usually ~1 h 10 m (12 runs) · ~25 m left". Never shown with fewer than 3 good runs.

**Actions:** B3 (Lower, Efficiency, Restore, End tree); Open folder (the script's folder from the command line, else the exe's); Open log (B4); Pin / Unpin as job (from A2's flyout row).

**Edge cases**
- PID reuse: key = (PID, creation time); a pinned PID that is reused is not a job.
- Short workers spawned and reaped (multiprocessing pools): the worker count changes; summed CPU uses a `RateCounter` per worker; gone workers drop out without spikes.
- A job whose root is not on the watch list (e.g. `make` → `python`): the topmost watch-list process is the root; its parent's A2 group is shown.
- Elevated jobs: listed by name only (no command line); actions greyed with A3's reason.
- `py.exe` launcher → `python.exe`: the launcher is not on the list, so the child becomes the root.
- Exit code 259: shown as "exited (259)" because it is read after the wait fires.
- Many jobs (e.g. 40 test processes): S/M/L show top N; the flyout groups jobs by parent app when > 10.

**Acceptance**
1. A test script burning one core appears as a job 60–180 s after start; an idle `node --stdio` helper running for 1 h never does (replay of recorded series + the reference-PC probe).
2. A job that drops to 0 CPU stays listed until it exits (test).
3. Workers fold under the root; the folded cores equal the sum of the workers (unit test).
4. The exit shows the right exit code within 1 s (test).
5. A Normal-priority test job using ≥ 6 cores for 2 min with system CPU ≥ 80% gets the hogging badge; the same job at BelowNormal, or with system CPU < 80%, does not (test with recorded series).
6. With no job and no viewer, B does at most one process snapshot per minute (counter test, D-B8).
7. A kind seen idle 5 times is probed every 10 min, yet a run of it that starts burning CPU still becomes a job within 10 min (test, fake clock).
8. After 3 good runs of a fixture kind, a new run shows an ETA equal to the median duration; with 2 runs it shows none (test).
9. A non-listed exe busy for 2 min under a terminal while the flyout is open produces one "Watch as job?" suggestion, never repeated after a No (test).

## B2. Silent-job flag + toast
**Status:** accepted.

**Definition (D-B5).** For each job, "evidence of work" in a 10 s read is any of: CPU ≥ 0.02 cores; disk read + write ≥ 64 KB; progress file updated; named log file grew. Thresholds are rule values.
- **Silent** = no evidence on every signal for longer than the kind's **learned quiet threshold** (`LearnedThreshold`; Adaptive core), fallback **10 min**, clamped 2–60 min. Before flagging, one GPU read for that PID: GPU busy → stays "working (GPU)" and that time does not count as quiet.
- **Stuck-busy** (B4 jobs only) = CPU busy but `updated` unchanged for 2 × `heartbeat_s` (the writer's own promise, as systemd's watchdog), or 10 min if the file declares none. Only progress files can tell a busy loop from real work.
- Back to **working** on the first read with evidence.

**What the user sees**
| Surface | Content |
|---|---|
| S | "1 silent" in warn colour |
| M / L | the row's status dot turns amber with "silent 12 min" (or "no progress 12 min" for stuck-busy) |
| Flyout | the same, plus which signals are quiet ("CPU 0 · disk 0 · log unchanged since 14:02") and the sparkline flat |
| Toast | "train.py has been silent for 14 min. Its longest normal pause is 9 min (12 runs). No CPU, disk or log activity." (or "… for 10 min · new job, default limit"). Buttons: **Show**, **Open log** (if known), **It's fine** (snoozes this run, labels a false alarm). No End in a toast (A6 precedent) |
| M / L / Flyout (B2.7) | "longer than usual: 1 h 50 m vs ~1 h 10 m" note on the row; no toast unless a rule asks for one |
| Toast (B2.5) | "train.py finished after 1 h 12 m (exit 0)" or "train.py failed after 14 m (exit 1)". Buttons: **Open folder**, **Open log**. Only for jobs that ran ≥ 10 min, and only if the job's window (its console host / terminal, found through the A2 tree) is not the foreground window at exit (one `GetForegroundWindow` check). Failed exits use the warn style |

**Rules (rules file, editable)**
| Rule | Condition | Default |
|---|---|---|
| Silent job | no evidence for the learned quiet threshold (fallback 10 min) | on |
| Longer than usual (B2.7) | elapsed > 1.5 × p90 of the kind's good runs (n ≥ 5) | on, note only |
| Guardrails (Adaptive core) | P1–P6 checks → `use_fallback` / `relearn` / report | on |
| Stuck-busy | progress file missed 2 × `heartbeat_s` (else 10 min) while CPU is busy | on |
| Finished | job that ran ≥ 10 min exits and its window is not in front | on |
| Hogging (B1.7) | Normal priority, ≥ 50% of logical CPUs, system CPU ≥ 80%, for 2 min | on, badge only (toast off) |
One silent toast per job per silence episode; a job silent again after resuming can toast again after 30 min.

**Edge cases:** a job waiting for input (e.g. `input()` in a terminal) is correctly silent, and the toast says what is quiet, not "hung"; network-bound jobs (downloads) may show no disk I/O until they write; whether network bytes are in the I/O counters is **[unverified, W1]**, so the toast names the signals rather than guessing; sleep / hibernate pauses the window (time asleep does not count, from the power events A8 already gets); Focus / Do not disturb are honoured by Windows; jobs ended by Tasktor or the user do not raise a "failed" toast.

**Acceptance**
1. A test job of a **new kind** (fallback) that sleeps 11 min raises exactly one silent toast after 10 min; it clears when the job resumes (fake clock). Learned thresholds are covered by the Adaptive core acceptance tests.
2. A job that only writes to its log (no CPU) is never silent (test).
3. A job with GPU busy and CPU idle is not flagged (mocked PDH).
4. A busy-looping job with a stale progress file is flagged stuck-busy; without a progress file it is not; with `heartbeat_s: 30` it is flagged after 60 s, not 10 min (test).
5. A 15-min job exiting with code 1 raises one "failed" toast with the exit code; a 5-min job raises none; a 15-min job whose terminal is the foreground window raises none (test with a fake foreground source).
6. A suspend of 30 min during a job does not make it silent on resume (fake power events).
7. Pressing **It's fine** records a false alarm for that kind and no further silent toast fires for that run (test).
8. The toast text states learned vs default and the basis numbers (snapshot test).

## B3. Priority and stop actions (via A3)
**Status:** accepted.

**What the user sees**
| Surface | Content |
|---|---|
| M / L | **Lower** on each row (or **Restore** when lowered); "…" → Efficiency mode, End… |
| Flyout | Lower / Efficiency / Restore / End… for the job tree, and per worker on expand; result line ("Lowered train.py and 4 workers", "2 workers run as admin: unchanged") |
| Toast | none of its own; the hogging badge (B1.7) puts **Lower** one click away |

**Actions and mechanism**
| Sub | Action | Mechanism | Evidence |
|---|---|---|---|
| B3.1 | Lower job | `SetPriorityClass(BELOW_NORMAL)` on root + every running worker; new workers inherit from a lowered parent | **[verified: SetPriorityClass; Scheduling Priorities]** |
| B3.2 | Efficiency mode | A3.4 (EcoQoS + low priority) on the tree | **[verified: SetProcessInformation]**; Task Manager equivalence = A's OQ6 |
| B3.3 | Restore | original class per process (remembered at first change) | design |
| B3.4 | Auto-restrain (off) | ProBalance-style rule: while hogging (B1.7) → Below Normal; when the system has been < 80% for 2 min → restore the remembered class; never the foreground app's job; each change logged and shown as a "restrained" badge. I3/I4 reuse the same action for "opted-in jobs" | design; priority ≠ throttle, so an uncontended restrained job runs at full speed **[source: bitsum.com CPU throttling doc]** |
| B3.5 | Cap cores (later) | a new empty Job Object per job tree with a hard CPU cap; joins even if the process is already in a job (nesting) | **[verified: AssignProcessToJobObject; CPU rate control]** |
| B3.6 | End job tree | A3.2, leaf-first, two-step confirm ("End train.py + 4 workers?") | A3 |
| B3.7 | Freeze / Resume | enumerate the tree's threads (Toolhelp thread snapshot, filtered by PID), `SuspendThread` each; repeat the enumeration until a pass finds no new running thread (a thread can be created mid-pass); remember exactly which threads Tasktor suspended and resume only those, once each. Optional **Free RAM**: `EmptyWorkingSet` per process after the freeze | **[verified: SuspendThread, EmptyWorkingSet]**; behaviour on a CUDA job **[unverified, W1 probe]** |
| B3.7 | Polite pause / Resume | Tasktor writes `jobs\<id>.control.json` `{"pause": true}` (its own file, separate from the job's progress file, so schema v1 is unchanged); the B4 helper checks it in `update()` and blocks there until `pause` is false, writing `"paused": true` (an additive field) to its progress file | design |
| B3.6 | Stop gracefully (console jobs) | jobs usually have no window, so A3.0's `WM_CLOSE` does nothing; a Ctrl+C to the job's console would let it checkpoint **[unverified: attach-to-console + `GenerateConsoleCtrlEvent` from the agent, W1 probe]** | — |

**Pause job (B3.7) in detail**
| Mode | Works on | Frees while paused | Resume | Risk |
|---|---|---|---|---|
| Freeze | any job Tasktor may act on | CPU at once; RAM if "Free RAM" is ticked (paged back slowly on resume); **not GPU memory** | exact: continues mid-instruction | network jobs can time out (remote data, distributed training); Tasktor itself never takes the job's locks, so the documented deadlock case does not apply |
| Polite pause | jobs using the B4 helper | CPU; whatever the job chooses to release | the helper returns from `update()` | none: the job picks the stop point |
| Checkpoint and stop | Tasktor's refit (B5.3); user jobs that checkpoint themselves | everything, including GPU memory; survives a reboot | rerun from the checkpoint | work since the last checkpoint |
The row offers **Pause** (polite when the job uses the helper, else freeze) and "…" → **Freeze now**. Paused rows show "paused 12 m" and **Resume**.


**Interaction with the rest of B:** paused time is not quiet time (B2 silent and stuck-busy timers stop); it is excluded from the run's quiet-gap and duration statistics (Adaptive core), and a paused run's ETA adds the paused time. A resumed job re-enters "working" without a toast.

**Guards (A3's `GuardChain`, unchanged):** own user and same integrity only; protected and denylisted processes greyed; never elevate; fresh (PID, creation time) re-check before each call. Priority changes are reversible, so they need **no** confirm; End keeps A3's two-step. Each action is logged (H6) without the command line.


**Edge cases:** a pool that respawns workers from an unlowered parent (lower the root, not only the workers, which B3.1 does); mixed admin workers (partial result message, as in A3); the job raising its own priority back (shown, not fought); lowering a job whose parent is the terminal does not lower the terminal.

**Acceptance**
1. Lower on a job with 4 workers sets all 5 to BelowNormal; a worker spawned afterwards is BelowNormal too (test).
2. Restore returns every process to its original class (test).
3. An elevated worker is skipped and reported, with no call attempted (manual).
4. End shows the two-step confirm and ends leaf-first (UI test).
5. Auto-restrain is off on a fresh install; when on, a hogging job is lowered within one read of the condition and restored within 2 min of contention ending; a job whose window is in front is never restrained (test).
6. After you lower a kind by hand in 3 of 5 runs, one "Always restrain …?" suggestion appears; accepting creates a rule scoped to that kind only; declining suppresses it for 30 days (test).
7. A restrained job starved to ~0 cores while the system is idle is restored within 2 min (P6 guardrail test).
8. Freeze on a job with 4 workers suspends every thread of all 5 processes (CPU time stops growing); Resume restores each thread's suspend count to its prior value; a thread the job had suspended itself stays suspended (test).
9. Killing the agent while a job is frozen, then restarting it, resumes the job within 5 s of start (test).
10. A polite pause on a fixture job using the helper stops its progress at the next `update()` and resumes on Resume; its progress file shows `"paused": true` meanwhile (test).
11. A frozen job is never flagged silent, and its frozen time is absent from the kind's statistics (test, fake clock).
12. W1 probe: a PyTorch training job (CUDA, DataLoader workers) frozen for 30 min resumes and finishes with the same loss curve as an unpaused run; record whether VRAM stays allocated; also record that `OpenThread(THREAD_SUSPEND_RESUME)` succeeds non-admin on own-user threads and is refused on elevated ones (the access-right check A3.7 shares) (manual).

## B4. Progress files (`jobs/<id>.json`)
**Status:** accepted. Folder virtualization exclusion still waits on H's sign-off and a W1 test.

**Where:** `%LOCALAPPDATA%\Tasktor\jobs\` (H's proposed layout, unsigned). For the MSIX build the agent must see files written by unpackaged scripts at the real path, so the folder is excluded from AppData virtualization (H's fix, `virtualization:ExcludedDirectories`) **[unverified until H signs; W1 test]**. The portable build uses its data folder.

**Schema v1** (JSON, UTF-8, ≤ 4 KB; unknown fields ignored, so writers can add their own)
```json
{
  "schema": 1,
  "id": "embed-2026-11-04",
  "pid": 12345,
  "label": "embed corpus",
  "pct": 42.5,
  "done": 2380, "total": 5665, "unit": "docs",
  "stage": "embedding",
  "msg": "batch 75/178",
  "eta_s": 1800,
  "log": "C:/work/embed.log",
  "state": "running",
  "heartbeat_s": 30,
  "updated": "2026-11-04T14:02:11Z"
}
```
| Field | Rule |
|---|---|
| `schema` | required, `1` |
| `id` | required; equals the file stem; `[A-Za-z0-9._-]{1,64}` |
| `updated` | required, ISO-8601 UTC; it is the heartbeat (B2) |
| `pid` | optional; links the file to a process row. Without it, a file-only row is shown |
| `pct` or `done`+`total` | optional; `pct` is clamped to 0–100; if both are given, `done`/`total` wins |
| `label`, `stage`, `msg`, `unit` | optional, plain text, ≤ 120 chars each, shown as text only |
| `eta_s` | optional; if absent, Tasktor estimates from the `pct` rate after 3 updates |
| `log` | optional local file path; enables Open log and log growth (B2). URLs are rejected |
| `state` | OSC 9;4 vocabulary: `running` (default, OSC 1 normal) / `indeterminate` (OSC 3, no percent) / `warning` (OSC 4, still running but degraded) / `error` (OSC 2, failed) / `done` (OSC 0, finished). Shared names mean the helper's file and its terminal progress never disagree |
| `heartbeat_s` | optional, 5–3600; the writer's promise to update at least this often (systemd watchdog idea). Stuck-busy / silent = no update for 2 × `heartbeat_s`; absent → the 10-min default |

**Writer rules (published in the repo docs):** write to `<id>.json.tmp`, then rename over `<id>.json` (atomic replace), so Tasktor never reads a half-written file; at most about 1 write per second (faster writes are debounced, not rejected); delete the file or set `state` when finished.

**Reader:** `ReadDirectoryChangesW` on the folder (file name + last write); on overflow, re-enumerate (D-B8). Each changed file is read at most once a second, validated against the schema, and turned into a `ProgressRow`; invalid files show one "invalid progress file" row with the reason, never an error toast.

**Linking to processes:** with `pid`, the file attaches to that job row and bypasses the gate (B1.3); the PID is valid only if the process started before the file's first `updated` (PID-reuse guard). Without `pid` (e.g. a remote or WSL job **[unverified for WSL paths]**), a standalone row shows the progress and uses `updated` for silence.

**Lifecycle:** `done` / `error` rows behave like finished jobs (10 min, B2.5 toast if long). Files whose process is gone and that have not been updated for 24 h are deleted by Tasktor (it owns the folder); nothing outside `jobs\` is ever touched.

**Writers (B4.7):** a stdlib-only Python helper (`tasktor_progress.py`, ~30 lines: `Progress(id, total, heartbeat_s=30).update(done, msg)`) and `tasktor job set <id> --pct 42` (D's CLI). The helper writes the file **and**, when stdout is a terminal that supports it (Windows Terminal sets `WT_SESSION`), prints `ESC ] 9 ; 4 ; state ; pct BEL`, so the tab ring and taskbar show the same progress **[verified: Windows Terminal progress-bar sequences doc]**. Tasktor itself never reads terminal output. Both writers are documented examples, not dependencies; any language can write the file.

**Security:** files are untrusted input (any local process can write them). Size cap before parse; source-generated JSON with depth limits; text rendered as text (no markup); `log` is opened only when the user clicks, in the default viewer, and never executed; no field names a command.

**What the user sees:** the progress bar, `done/total unit`, stage and message on the job row (B1); ETA in the flyout; S shows the single job's percent.

**Edge cases:** a file written on every iteration (debounce); a clock-skewed `updated` in the future (treated as now); two files naming one PID (both stages shown on one row); the folder deleted while running (recreated, watch restarted); OneDrive or network data folders (not supported; the folder is local).

**Acceptance**
1. A script writing the example file shows a 42% bar on its job row within 1 s (test).
2. A half-written file (no rename) is never read; a renamed one is (test).
3. A 1 MB file is rejected unread; a file with an unknown field is accepted (test).
4. 100 writes a second cost < 1% agent CPU (D-B8 test 3).
5. Overflowing the watch buffer triggers a re-enumeration that loses no file (test).
6. In the MSIX build, a file written by an unpackaged `python.exe` is seen (manual, W1).

## B5. Tasktor's own long tasks as jobs
**Status:** accepted. Refit row ships in L2; the folder-scan row ships with A5 in W1.

**What the user sees**
| Surface | Content |
|---|---|
| M / L | "Refit · stage SVD · 40%" row with a Tasktor icon, a "limited" badge (BelowNormal + EcoQoS, 25% CPU, 1.5 GB) |
| Flyout | stages done / to do (features → SVD → fits → bootstrap → eval → export, from C8); elapsed; result link to the eval summary (C's `eval.json`, via `tasktor clusters --eval`); **Cancel**, **Pause** |
| Toast | none for success (refits are routine); one toast if a refit fails twice in a row ("Workflow refit failed: open log") |

**Data source:** `WorkerProgressSource` reads the worker's stdout JSON lines (`{stage, pct, msg}`, C8) through the pipe the agent already owns, into the same `ProgressRow` as B4. No file and no polling. The A5 folder scan reports through the same row type from inside the agent.

**Actions:** **Cancel** ends the worker through the agent's own child-process handle (the worker is not subject to A3's denylist, which protects `tasktor-core` itself); **Pause** = cancel now, and the next run resumes from the last checkpointed stage (C8 checkpoints stages), so nothing is lost. B3 actions (Lower etc.) are hidden: the worker already runs at its limits.

**Edge cases:** the worker's watchdog timeout (shown as "failed: timeout"); a refit deferred by I3/I4/I13 ("waiting: on battery"); stdout lines that are not JSON (ignored, kept in the log).

**Acceptance**
1. A fake worker emitting stage lines moves the row through every stage (test).
2. Cancel ends the worker within 2 s and the next refit resumes from the last stage (test with a fake worker).
3. The refit row never offers Lower or End (UI test).

## B6. Job kinds: what Tasktor learned
**Status:** accepted. Core, W1.

**What the user sees**
| Surface | Content |
|---|---|
| Flyout → Jobs → "Learned" | one row per kind currently or recently running (names are shown only while known from a live job; others show "kind seen 3 days ago"): runs, usual duration, silent threshold with learned/default badge, false alarms, mode (learned / using default since …) |
| Row detail | the guardrail log for that kind ("switched to default: 4 of last 10 alarms were false · 2026-11-12"), **Relearn**, **Forget** |
| Settings | "Learn from my jobs", **Forget everything learned** |


**Edge cases:** a script renamed (new kind, starts on fallback); the same script under a different interpreter (`python` vs `pythonw`: different kinds by design); the install key lost or reset (all kinds become new; old file discarded); many one-off kinds (LRU keeps the busy ones).

**Acceptance**
1. Forget removes the kind from memory and from `job-kinds.json` on the next write (test).
2. With learning off, the Learned view says so and every decision shows "default" (UI test).
3. Every REPLACE / relearn appears in the guardrail log with its reason (test).

1. `JobRow` and `ProgressRow` (records): fields, which are optional, how a `ProgressRow` attaches to a `JobRow`.
2. `JobGate`: watch list + excludes + windowed check + pins + bypass; the sticky invariant; what it caches per (PID, creation time).
3. `JobWatch` (Feature): its leases (60 s / 10 s / 1–2 s), how it consumes the table diff, exit events and progress rows, the `JobEvent`s it emits, the card it builds.
4. `ProcessExitEvents`: handle ownership (who closes what, when), the one-shot wait, unregister-before-close.
5. `JobProgressSource`: watch loop, debounce, overflow re-enumeration, validation.
6. `KindId` + `KindStats`: hashing (what goes in, what never does), the rings, the update at job end, labels, LRU, persistence.
7. `LearnedThreshold`: learned value, blend toward the fallback, clamp, `source`, caching per run.
8. The guardrail rules (P1–P6) as `RuleEngine` rules with `use_fallback` / `relearn` / report actions, including the 5-clean-runs hysteresis.
9. The silent / stuck-busy rules as compositions of `LearnedThreshold` + `HoldFor`.

## Open questions
Decided enough to review; these are checks to run, not decisions.
| # | Question | Resolved by |
|---|---|---|
| OQ-B1 | Cost of a names-only Toolhelp snapshot every 60 s on the reference PC (expected ms) | A0 |
| OQ-B2 | WMI from the Native AOT agent (COM client) for command lines; fallback: read the PEB with `PROCESS_VM_READ` (documented struct, "may change") | W0 spike (with A's COM check) |
| OQ-B3 | `GetProcessTimes` with `PROCESS_QUERY_LIMITED_INFORMATION` (shared with A2) | A0 |
| OQ-B4 | Do I/O counters include network and console writes (affects B2's "disk" signal wording) | W1 |
| OQ-B5 | Graceful Ctrl+C to a console job from a windowless agent | W1 probe |
| OQ-B6 | MSIX: `jobs\` excluded from AppData virtualization so unpackaged writers and the packaged agent see one folder | H session, W1 test |
| OQ-B7 | Rules file format: A says `rules.json`, H (unsigned) proposes `rules.toml`; B says "the rules file (H10)" | H session |
| OQ-B8 | ~~Silent default 10 min vs 5 min~~ → superseded: 10 min is now only the fallback; the learned threshold decides once a kind has history (D-B5) | — |
| OQ-B9 | Cooperation-free progress (Xfennec `progress` style: open file offset vs size, e.g. ffmpeg reading one input). Needs another process's handle list, which is undocumented on Windows | later / Pack |
| OQ-B10 | Reading another window's taskbar progress (what OSC 9;4 drives in Windows Terminal): no documented API found in this pass; a search finding nothing is not proof | W1 check, else drop |
| OQ-B12 | ~~Keyed hash of exe + script name vs exe only~~ → exe + script hash; `00-frame.md` privacy rules to add the line in Changes for the catalog | — |
| OQ-B13 | ~~Learning on by default vs opt-in~~ → on by default | — |
| OQ-B14 | Are the starting constants right (1.5 × max quiet gap, trust ramp 5 → 20 runs, 30% false-alarm limit, 5 clean runs)? Replay the user's real job history once W1 logs exist, pre-registered before looking | W1 + 2 weeks of logs |
| OQ-B15 | DPAPI for the install key (user scope), or a plain random file | W1 |

## Consolidated at signing: architecture decisions and what they change elsewhere (2026-11-04)
Section B is signed; the sections below are **not** edited by this session. Each row is a change their owner should make or decide. "Must" = B depends on it; "can" = optional reuse.

**B's architecture decisions in one place**
| Id | Decision |
|---|---|
| D-B1 | Job = watch-list exe, age ≥ 60 s, ≥ 30 s CPU in the last 2 min; sticky; pin or progress file bypasses |
| D-B2 | Always on: names-only scan every 60 s, handles only for watch-list matches |
| D-B3 | Two views of the same processes: A2 keeps them under the launching app, B shows the job tree |
| D-B4 | Exits are pushed (one wait per job), with exit codes |
| D-B5 | Silent = quiet longer than this kind's normal; 10-min fallback; trust ramp 5 → 20 good runs; clamp 2–60 min |
| D-B6 | The progress file is a public, versioned contract (schema v1 in Contracts, untrusted input) |
| D-B7 | CPU shown in cores |
| D-B8 | B fits inside D-A3: ≤ 0.5 MB and one snapshot a minute when idle |
| D-B9 | Adaptive by design: learned per-kind value + fixed fallback + LDOS-style guardrails (P1–P6 → report / replace / relearn) |
| D-B10 | Learned data stores no names: kind = keyed hash of exe + script; numbers only; forgettable; learning on by default |
| D-B11 | Type design: `JobWatch` façade over 8 Core classes; no new interface, block or assembly; 5 `IProcessControl` members; one logic thread |
| B3.7 | Pause job: freeze (any job) or polite pause (helper jobs); crash-safe resume |

**A (system monitor): changes needed**
| # | Change | Kind |
|---|---|---|
| A-1 | `ProcessTableSource` / `DemandLease` gain a **column set** parameter (names-only: exe, PID, parent) so B's 60 s scan opens no handles | must |
| A-2 | `TreeRollup`'s anchor predicate must be a parameter (D-A6 already describes it as one); B passes "topmost watch-list ancestor" | must (confirm) |
| A-4 | `RuleEngine` gains the `LearnedThreshold` condition class and the `use_fallback` / `relearn` actions | must |
| A-5 | Threading rule: one logic thread in the agent; sources only enqueue onto `EventChannel`; actions run off-loop and post results back. A's sources (PDH, power, low memory) follow it too | must (project-wide, with H) |
| A-6 | D-A5 table: `RingBuffer` is used by B for the 10-min **sparkline**, not a "silent-job window" (stale wording) | must (text) |
| A-7 | A2's flyout offers **Watch as job** (pin, B1.3); while a viewer is open, B reads A2's per-process data for the "Watch as job?" suggestion (B1.9) at no extra sampling | must |
| A-8 | B reuses A's `PowerEvents` (sleep spans excluded from quiet time), the per-PID GPU read from `PdhMetricSource`, and `AppIdentity`'s rule matcher in `JobGate` | must (already shared) |
| A-9 | A8.2 reads `jobs.running` from `JobWatch` | must (already planned) |
| A-10 | A3 offers **Freeze** on any app row through the same members | adopted in A as A3.7 |
| A-11 | A6 (per-app memory growth) and A5 (disk-fill rate) can adopt `KindStats` + `LearnedThreshold` + guardrails for a learned "normal" with a fixed fallback | adopted in A: A5 fill rate per volume (fallback ≥ 5 GB/h), A6 growth per app (fallback +500 MB/min) |

**Shared core and Contracts (project infrastructure)**
- New Contracts records: `ProcKey`, `JobId`, `KindId`, `JobRow`, `WorkerRow`, `ProgressRow`, `JobEvent`, `LearnedValue`, `ControlRequest`, and the progress-file schema v1.
- Still 7 interfaces, 4 assemblies, no new block kind.

**C (workflow learning)**
| Change | Kind |
|---|---|
| Privacy rules gain: "learned per-kind job statistics are numbers only, keyed by a hash; forgettable" (C's own rule, command lines never stored, still holds) | must |
| The learning split becomes explicit: **online statistics in C# Core** (B; maybe A6) vs **model fits in the Python worker** (C). LDOS's overhead lesson is the reason | must (Frame text) |
| B5 shows refit progress from C8's stdout `{stage, pct, msg}` and links C's `eval.json`; C8's stage names and checkpoints are what Pause resumes from | must (C keeps that format) |
| `ProcessExitEvents` / `JobEvent` give C exact episode boundaries for long tasks | can |

**D (integration)**
| Change | Kind |
|---|---|
| Snapshot `jobs[]` fields follow `JobRow`: status (10 states), pause mode, cores, progress, learned/default source | must (WS0) |
| `tasktor job` CLI writes progress files for non-Python scripts (B4.7) | must |

**E (glance modules)**: no change. `ProgressRow` and the `Progress` block can carry any long task a glance source reports (can).

| Change | Kind |
|---|---|
| B2.5 needs "is this job's window in front", including Windows Terminal tabs (OQ-B11); F's window events are the natural source | must (shared answer) |
| A frozen GUI app's window looks "Not responding"; F must not act on hung-looking windows of frozen jobs | must |

**G (surfaces)**
| Change | Kind |
|---|---|
| Tasktor Jobs card adds **Pause / Resume**, a "paused 12 m" state, and the learned/default badge; M/L rows show ETA and "longer than usual" | must |
| Flyout gains the B6 **Learned** view (per kind, guardrail log, Relearn, Forget) and Settings "Learn from my jobs" / "Forget everything learned" | must |
| Uses existing blocks only (`Table`, `Progress`, `Badge`, `Sparkline`, `Actions`, `Alert`) | — |

**H (platform)**
| Change | Kind |
|---|---|
| H10 grammar adds the `LearnedThreshold` condition, `use_fallback` / `relearn` actions, and `pause` gains a `job` target (B3.7) next to refit / tracking / plugin | must |
| One-logic-thread agent rule (A-5) recorded as a platform rule | must |
| H5 budgets carry B's share (≤ 0.5 MB idle, ≤ 1 MB more with jobs); H6 logs guardrail events and actions without command lines | must |
| H's MSIX fix (exclude `%LOCALAPPDATA%\Tasktor` from virtualization) covers `jobs\`, so B4.1's caveat resolves with H's V1 check | — |
| Export excludes learned state unless `--include-learned` | must |

**I (tailored)**
| Row | How B applies | Kind |
|---|---|---|
| I13 Event mode | `KindStats` + `LearnedThreshold` for a learned normal of recurring timings | can |

**00-frame.md and 01-catalog.md**
- `00-frame.md`: Stack paragraph still says Rust (stale since OQ12); add the privacy line (C row above) and the learning split.
- `01-catalog.md`: B rows per "Changes for the catalog" below (B3 rename + B3.7, B5 widened, B6 added, finished toast in B2, hogging badge).

## Changes for the catalog
_(for the parent session to merge into `01-catalog.md`, `00-frame.md`, `WORKSTREAMS.md` and the shared-core notes)_
**Renamed / reshaped**
- B1: windowed job gate (≥ 30 s CPU within 2 min, age ≥ 60 s, sticky; pin + progress-file bypasses); CPU in cores; exit codes; hogging badge (B1.7).
- B2: adds stuck-busy (progress files) and the finished / failed toast (B2.5).
- B3: rename to "Priority and stop actions"; tree-wide Lower / Efficiency / Restore through A3; auto-restrain rule (ProBalance-style, off); End tree via A3.2. Core cap (Job Object) → later, with I4.
- B4: schema v1 as above (states use OSC 9;4 names; optional `heartbeat_s`); public contract owned by WS0; Python helper (file + OSC 9;4) + `tasktor job` CLI as examples.
- Prior art folded (2026-11-04): ProBalance two-gate hogging + temporary auto-restrain (B1.7, B3.4); fish `done` foreground check for the finished toast (B2.5); systemd-style heartbeat (B4); OSC 9;4 state names + helper output (B4).
- B3: add **B3.7 Pause job**; shared core: `IProcessControl` gains Freeze / Thaw / TrimMemory (OQ-B16).
- B5: widen to "Tasktor's own long tasks" (refit L2; folder-scan row with A5 in W1).
- **Section stance:** B is adaptive: per-kind learned policies, fixed rules as fallbacks, LDOS-style guardrails. Adds B1.8–B1.10, B2.7–B2.8, the learned suggestion in B3.4, and a new row **B6 Job kinds: what Tasktor learned** (Core, W1). D-B5 silent is now adaptive with a 10-min fallback.

**Cross-document changes**
- Shared core (WS1/WS2): `DemandLease` gains a column set (names-only process scans). Project-level change; affects A2, C.
- New Windows source `ProcessExitEvents : IEventSource<ProcessExit>`; new Contracts records `JobRow`, `ProgressRow`, `JobEvent`; progress-file schema in Contracts (WS0).
- `00-frame.md` C1.1: job watch's names-only scan runs on the 60 s tick always (D-B2); per-process handles only for watch-list matches. A second, narrow exception to "sample only while viewed", next to D-A1.
- `00-frame.md` C1.3: "silent-job flag" → silent + stuck-busy + finished; C1.4 alerts list gains "job finished / failed".
- H10 rules grammar: needs `job.new`, `job.silent`, `job.done` events and `jobs.running` state (A8.2, I3, I4 use them).
- H storage: `jobs\` must be excluded from MSIX AppData virtualization (OQ-B6).
- `00-frame.md` Stack paragraph still says Rust (stale since OQ12); B assumes C#.
- Shared core: `KindStats` + `LearnedThreshold` proposed as reusable Core classes (per-key learned normal with fallback); H10 grammar gains `use_fallback` and `relearn` actions and the `LearnedThreshold` condition.
- Threading (D-B11 §6): one logic thread per agent, sources only enqueue, actions off-loop. Proposed as a project-wide agent rule for H.
- Privacy rules (`00-frame.md`): add "learned per-kind statistics are numbers only, keyed by a hash; forgettable" (D-B10, OQ-B12).
- `00-frame.md` Frame: the learning split becomes explicit: **online statistics in C# Core** (B, possibly A6) vs **the Python worker for model fits** (C). LDOS's overhead lesson (P5) is the reason.
