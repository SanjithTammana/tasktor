# Tasktor — C. Workflow tracking and learning (feature spec)

**Status:** **signed**. Core tracking (T0, C1–C15) plus the deep-capture Module (T1, C16–C22) added the same day. D-C1–D-C22 decided; every  item was accepted at signing. Open items are probes, or belong to other sections (see Consolidated at signing).
**Sessions:** 2026-11-03 (first draft, written before A was signed, Rust stack); 2026-11-04 rebuilt on signed A and B (this file). Parent catalog: `../01-catalog.md` (rows C1–C15). Frame: `../00-frame.md` (C1.2 capture, C1.6 model runtime, C1.7 store, C2 learning pipeline, privacy rules). Workstreams: WS3 Event capture, WS6 Learning, WS9 (Python runtime packaging).
**Reference low end:** i7-1355U (10 cores / 12 threads), 12 GB RAM, Iris Xe, one monitor.

Conventions: **[verified: source]** = checked against Microsoft / library docs or a local probe this session; **[unverified]** = still a claim, checked in the named phase (A0 skeleton, W0 toolchain spike, A1 tracker build, L1 first clusters, L2 persistent models). **[estimate]** = a reasoned number to be measured. Sizes: **S / M / L** = Widgets-board small / medium / large card; **Flyout** = own window (G2). **** = proposed this session, not yet accepted.

**Why this section exists (the failure it prevents).** A system monitor can say *what the machine is doing*; it cannot say *what you are doing*. C turns the foreground stream into named, recurring workflows ("Tasktor dev", "course reading", "email triage") and time per workflow, learned from your own data with no labels. The risk it must not take is the one the user's horizon card names: **a confidently wrong inferred context is worse than none.** So every learned output in C has a safe fallback (the app you are in), a confidence floor, and pre-registered gates it must pass before it is shown.

**Design stance ( D-C5, extends D-B9).** C is the same adaptive pattern as B, at a larger scale: a **learned** answer (a workflow model fitted offline in Python), a **fallback** that is always true (the app, from `AppIdentity`), and **guardrails** (LDOS P1–P6) that decide which one the user sees. B learns a number per job kind with streaming statistics in C#; C learns a clustering with batch fits in Python and applies it online in C#. One framework, two learners.

##  Deep capture revision

**What the maintainer asked for (2026-11-04, the user's words condensed).**
- Record who is acting: the user typing, a CLI process, or an agent.
- Record keystrokes as a queue that is cached locally and then either thrown away or indexed into a small database for later lookup.
- Record which Chrome tabs and apps are open and what they show, and pull out and index that text.
- Treat mouse movement and media as inputs (example: a YouTube video playing while the user plays chess).
- Keep all of this in unstructured form and find workflows in it.
- Never use screenshots as the main way to see the screen.

**The user's answers to the scoping questions.**
- **Keystrokes:** the full raw stream. Password fields and sensitive data are suppressed, and the user can exclude any app.
- **Screen text:** UI Automation first, OCR only as a fallback. Change is detected by diffing what was read before; the design borrows from how agentic systems read desktops without taking repeated screenshots.
- **Retention:** raw content is kept 7 days, encrypted. The full-text index and the embeddings are kept longer. One command forgets everything.

### How agentic systems get desktop context without repeated screenshots (research, 2026-11-04)
| System | What it reads | What it means for C |
|---|---|---|
| **agent-desktop** (vercel-labs) | The OS accessibility tree as a JSON snapshot. Each element gets a ref id (`@snapshot:eN`). An `-i` filter keeps only interactive elements. A stale ref forces a re-snapshot. **[verified: README]** | **Adopt the snapshot shape**: a compact tree of role, name and value with stable refs. It stays a reference only (D-C14 stands); C reads UIA from its own code. |
| **agent-browser** (vercel-labs) | Over CDP, a compact text snapshot (`- link "Learn more" [ref=e1]`). `diff snapshot` compares accessibility-tree text line by line against the last snapshot or a baseline file and prints `+`/`-` lines and a summary. `diff screenshot` (pixel diff) is a separate command. **[verified: agent-browser.dev/diffing]** | **Adopt the line diff as C's change primitive**: C stores the first snapshot of a view, then only the `+`/`-` lines. Pixel diff is reserved for OCR surfaces. |
| **OpenAI Codex computer use** (Windows, since 2026-05-29, Codex 26.527) | Runs in the foreground. Reports disagree on whether it works mainly from screenshots or from the accessibility tree. OpenAI's demo stresses OS accessibility trees as the richer source, with screenshots as a fallback. **[unverified: secondary sources only; no OpenAI spec found]** | Confirms the order **tree first, pixels last**. Nothing more to take from it: it acts in the foreground, and C only watches. |
| **Microsoft UFO2** | UIA plus OmniParser-v2 (YOLO-v8 + Florence-2) for controls UIA does not expose. Results are merged by bounding-box overlap. **[verified: arXiv]** | Same order. C has no vision model in the loop. OCR covers only opaque surfaces (video, canvas, games). |
| **Windows Recall** (Copilot+) | A screenshot every few seconds when the screen changes, then local OCR plus embeddings into the **Windows Semantic Index**, encrypted with AES-256-GCM behind Windows Hello. Apps send **`UserActivity` + `ContentInfo`** (schema.org JSON, an `ActivationUri`, sensitivity labels) so Recall can relaunch content and skip sensitive windows. **[verified: MS Learn recall-relaunch / recall-content-info-labels; secondary for cadence]** | **Reject** the screenshot loop. **Adopt three of its parts**: the per-record encryption design, app-declared sensitivity as an exclusion signal, and the "relaunch from a record" idea (C stores the URL or document id, so a search hit can reopen it). |
| **Windows `AppContentIndexer`** (Windows App SDK 2.0 experimental) | A local lexical + semantic index (embeddings) of an app's **own** content. It is a Limited Access Feature, needs package identity and the `systemaimodels` capability, and is preview only. **[verified: MS Learn]** | **Not now.** It needs MSIX and a token, and it indexes only the app's own content. It is the right engine if Tasktor ever ships as MSIX on Copilot+ hardware (OQ-C24). |
| **screenpipe** | Accessibility APIs first, OCR as fallback. Captures are event-driven (app switch, window change, new content) and include keys, clipboard and clicks. **[self-reported]** | Closest prior art to the full ask, and validates the order. |

**Local probe (2026-11-04, counts and timings only; no text printed):**
- A cached UIA walk of the whole Chrome window found **90 elements** (browser chrome only, no `Document`). It took **297 ms cold and 135 ms warm**. Chrome's private memory did not move (5,362 → 5,364 MB). **[verified: local probe]**
- The web page itself was **not exposed** to this one-shot query. Chrome ≥ 138 has native UIA, but page accessibility turns on only when needed. **[verified: Chrome blog for native UIA; on-demand trigger unverified]**
- Conclusion: for the browser, UIA is the wrong source. C reads tabs and page text through a companion extension (D-C18).

**The no-screenshot rule.**
- C learns what is on screen from **structure, not pixels**, in this order:
  1. window events;
  2. app-native structure (UIA for native apps, the companion extension for the browser);
  3. media session metadata;
  4. pixels as a last resort.
- Pixels are read only for a **visible window whose UIA text is empty or useless** (video, canvas, game, remote desktop, image viewer), and only after it has held the foreground for ≥ 10 s.
- Pixels are read only when a cheap change test passes: a 64-bit perceptual hash of a ≤ 256 px thumbnail, compared with the last one, Hamming distance > 6.
- At most **one OCR per window per 30 s**.
- Expected result: **0 screenshots** for text apps and the browser. A YouTube + chess session costs **0** (media session + DOM) **[estimate, A1]**.

### Subfeatures added (, catalog rows proposed)
| # | Subfeature | What it records | Source | Size on screen |
|---|---|---|---|---|
| **C16** | **Actor attribution** | For each input burst and process start: actor ∈ {`human`, `agent:<app>`, `cli:<shell>`, `injected:unknown`} | LL-hook injected flags, B's process tree, B's job / agent watch | column on every record; Flyout filter |
| **C17** | **Key stream** | Typed text per (window, focused element) as a queue of key runs; edits applied (backspace, selection-replace) so the stored text is what was typed, not raw scan codes | `WH_KEYBOARD_LL` on a dedicated hook thread | Flyout search only |
| **C18** | **Screen text** | Per view: a snapshot of the visible text tree, then line diffs | UIA event handler group (native apps); companion browser extension (tabs, URL, readable DOM text, `MutationObserver` diffs); OCR fallback per D-C16 | Flyout search; card shows view titles only |
| **C19** | **Pointer and attention** | Mouse path downsampled to 10 Hz, clicks (button, target element role + name from UIA at click time), scroll, dwell per window, **all visible windows** with their screen share | `WH_MOUSE_LL`; window z-order sampled on change | features only; no card |
| **C20** | **Media and concurrent activity** | Now-playing (app, title, artist / channel, playing / paused, position) and which processes are making sound | `GlobalSystemMediaTransportControlsSessionManager` (SMTC) + WASAPI audio sessions | card line "▶ YouTube · <title>" beside the workflow |
| **C21** | **Content index** | FTS5 full-text index plus chunk embeddings over everything C16–C20 produce; search; "reopen" from a hit (URL, file, window) | content worker (C# indexer + batch Python embeddings) | Flyout "Search my activity" |
| **C22** | **CLI command log** | Commands typed into terminals, with exit status where available | PSReadLine history file, Windows Terminal shell integration marks (OSC 133) when enabled, child-process command lines of the terminal's shell | Flyout; feeds C16 |

###  D-C17 Actor attribution
- **Keys and clicks.**
  - `KBDLLHOOKSTRUCT.flags & LLKHF_INJECTED (0x10)` and `MSLLHOOKSTRUCT.flags & LLMHF_INJECTED (0x01)` mark synthetic input. `LLKHF_LOWER_IL_INJECTED (0x02)` adds that the injector ran at a lower integrity level. **[verified: MS Learn KBDLLHOOKSTRUCT; MSLLHOOKSTRUCT flag value unverified, A1]**
  - Hardware input is `human`.
  - Remote desktop and some accessibility tools also inject. The `injected:unknown` label is honest; C never guesses a name.
- **Processes.**
  - A process start is `cli:<shell>` when its parent chain reaches a shell (`pwsh`, `powershell`, `cmd`, `bash`, `wsl`) **and** a key run ended with Enter in that terminal window ≤ 2 s before.
  - It is `agent:<app>` when the chain passes through a known agent process.
  - Otherwise it is `app` (spawned by an app) or `system`.
  - The parent and command line come from B's names-only scan plus one `Win32_Process` command-line read for the current user's processes, done only for shells' children. **[unverified: non-admin command-line read for own processes, A1]**
- **What it is for.** C splits workflows by who drives them. "an AI agent refactors while I read docs" is two tracks, and time per workflow counts only `human` time by default.

###  D-C18 Screen text: event-driven snapshots + line diffs
**Native apps (UIA).**
- One **UIA client thread** (MTA, its own COM apartment, off the logic thread per D-B11) registers one **event handler group** (Win10 1809+). Handlers are never added or removed from other threads **[verified: MS Learn]**. The group covers:
  - focus changed;
  - `TextChanged` / `TextEditTextChanged` on the focused window's root;
  - `StructureChanged` (children invalidated);
  - `NameChanged`.
- On an event, debounced 750 ms per window: one **cached** walk (`CacheRequest`, `TreeScope.Subtree`) of the changed element's nearest container. The walk fetches Name, ControlType, Value (if not password), IsPassword, IsOffscreen and BoundingRectangle; it is limited to depth 12 and ≤ 400 elements and skips offscreen subtrees.
- The probe shows a cold whole-window walk at ~300 ms and warm at ~135 ms. Subtree walks are smaller **[estimate, A1]**.
- **Snapshot format (agent-desktop / agent-browser shape):** one line per element, `<indent><role> "<name>" [= "<value>"] [ref=<stable id>]`. The ref is a hash of (AutomationId or role + name + sibling index along the path), so it survives re-walks.
- **Diff:** each walk is compared line by line (Myers) with the stored snapshot of that view. Only `+` and `-` lines go to the raw store.
  - A view is (window, root element ref), so tabs inside one app are separate views.
  - A full snapshot is re-stored every 50 diffs or when the diff exceeds 60% of the lines.
- **Text bodies:** `TextPattern` on Document / Edit elements. Visible ranges only (`GetVisibleRanges`), capped at 8 KB per read, and only after 2 s of dwell. Scrolling = a new visible range = a diff.

**Browser ( companion extension, MV3, Chrome + Edge).**
- Tab events (created, activated, updated URL / title, closed, audible, incognito).
- The full URL is recorded unless the host is excluded.
- Page text = a readable-content extraction (main content, headings, visible viewport text), sent on load + 2 s dwell. After that, `MutationObserver` batches are sent at most every 2 s as text diffs.
- Typed text is not read here; C17 owns keys.
- Transport: **Chrome native messaging** to a small `tasktor-capture` host (stdio JSON).
- Incognito / InPrivate: **not read** unless the user turns it on per profile.
- Why not UIA for Chrome: the probe found no exposed page, and forcing renderer accessibility for all tabs is a known memory and CPU cost in Chrome **[unverified, A1]**.
- Why not CDP: it needs `--remote-debugging-port`, which exposes the browser to any local process. Rejected.

**Pixels (fallback, D-C16).**
- Windows.Graphics.Capture, single frame of the one window. DirtyRegionMode is prerelease, so C uses the perceptual-hash test instead.
- OCR runs in the **content worker**: RapidOCR / PaddleOCR ONNX models on CPU, batched.
- Windows.Media.Ocr is rejected for now: it needs package identity (MSIX), and Tasktor ships unpackaged (OQ-C24).

###  D-C19 Key stream as a queue
- **Hook.** One `WH_KEYBOARD_LL` + `WH_MOUSE_LL` hook thread with its own message loop. The callback copies (vk, scan, flags, time) into a lock-free ring (64 K entries, ~1.5 MB) and returns.
  - Callback budget ≤ 50 µs. Windows silently removes a hook that exceeds `LowLevelHooksTimeout` (≤ 1000 ms on 1709+) **[verified: MS Learn]**.
  - A watchdog re-installs the hook if no event arrives for 30 s while `GetLastInputInfo` shows input.
- **Queue → runs.**
  - The capture thread drains the ring every 100 ms and groups keys into **runs**: same window + same focused element, ending at a 3 s pause, a focus change, or Enter.
  - Each run is decoded with the current keyboard layout (`ToUnicodeEx`), and edits are applied, so a run stores typed text, keystroke count, backspace count, duration and actor.
  - Shortcuts (Ctrl/Alt/Win + key) are stored as chords, e.g. `Ctrl+S`, not as text.
- **Suppression, checked when the run opens and on every focus change.** If any check below fires, the run stores **nothing but its length and duration**:
  1. The focused element has `IsPassword = true` (UIA, cached on focus change).
  2. The secure desktop is active: Winlogon / UAC prompts are not on the user's desktop, so the hook sees nothing **[verified: by design of desktops]**.
  4. The browser extension says the field is `type=password` or `autocomplete` is `cc-*`, `one-time-code` or `current-password`.
  5. An app declared the content sensitive through `UserActivity` `ContentInfo` labels, where readable **[unverified: whether a third party can read another app's UserActivity, A1]**.
- **Redaction after capture, before anything is written:** regex + checksum detectors for card numbers (Luhn), IBANs, US SSNs, API keys and tokens (known prefixes + high-entropy runs ≥ 32 chars), private-key blocks and JWTs. A match is replaced by `⟦redacted:kind⟧`. Recall's experience is that pattern filters leak, so **exclusion lists are the real control and redaction is a second net** **[verified: Recall support page; Reclaim The Net test]**.
- **"Cached until we know what to do with it" = the 7-day raw store.** Runs land in encrypted raw storage. Within 7 days the content worker indexes them (C21). On day 8 the raw text is deleted, and only index entries (FTS terms + embeddings + the record's metadata) remain.

###  D-C20 Unstructured representation: the activity document
- Every **activity** (D-C1) gets a plain-text **activity document**, rebuilt as it grows:

```
[2026-11-04 14:02–14:19 · human · foreground]
app: Chrome · tab: lichess.org · "Game vs …"
saw: <diff-compressed visible text, ≤ 2 KB>
typed: <key runs, ≤ 1 KB>
clicked: button "Resign"? no · link "Analysis board"
alongside: ▶ YouTube "…" (audio, background tab, 17 min)
pointer: 212 clicks, 38 scrolls, dwell 92% this window
```

- **Concurrent tracks.** The foreground activity is track 1. Anything producing sound or visible in ≥ 25% of the screen for ≥ 60 s is a **side track** with its own document. "YouTube while chess" is therefore a **compound workflow**: a co-occurrence of two clusters, not one blended cluster.
- **Features change accordingly** (this revises D-C2 / C5).
  - The primary representation is a **sentence embedding of the activity document**: a small local model, e.g. a MiniLM-class ONNX encoder with 384 dims **[model choice open, OQ-C21]**.
  - It is concatenated with the existing structural features (app one-hot, time of day, dwell, switch rate, pointer stats, actor share).
  - Hashed TF-IDF (D-C4) is kept as the cheap path and as the null-model comparison in G6.
  - Workflows are still windows of activity clusters, and C12's sequence models (PrefixSpan) get richer tokens.
- Gates G0–G7 and `prereg.json` apply unchanged. A new gate **G8** requires the embedding model to beat hashed TF-IDF on the hand-labelled 3-day check set (horizon card) by ≥ 0.05 ARI; otherwise C ships TF-IDF.

###  D-C21 Processes and budgets (revises D-C7 for the Module)
| Process | Lifetime | Does | Budget |
|---|---|---|---|
| `tasktor-agent` (D-A3) | always | unchanged: window events, episodes, online assignment | **unchanged: ≤ 15 MB idle / ≤ 0.1% CPU**. Deep capture adds nothing here |
|  content worker (Python, D-C11 rules) | batch: every 30 min while on AC and idle ≥ 2 min, or nightly; never resident | OCR of queued frames, chunk + embed activity documents, FTS5 merge | Job Object 25% CPU / 1.5 GB; ≤ 5 min per run |
- Why a separate capture process: the hooks need their own message loop, and UIA COM calls can block for hundreds of ms in a hung app, which must never stall the agent's logic thread (D-B11). It also lets the Module be killed or uninstalled without touching A.
- Cost with the Module off: **zero**. The process does not exist.

###  D-C22 Privacy tiers for the Module (rewrites Frame privacy rule 2 for this Module only)
| Tier | What | Default |
|---|---|---|
| T0 Core tracking (C1–C15) | app, redacted title, hostname, idle, process start / stop | off; Module toggle |
| T1 Deep capture (C16–C22) | full URLs, screen text, key runs, pointer, media, CLI commands | **off**, separate consent screen, separate toggle |
| T2 Pixels (OCR fallback) | single-window frames, deleted after OCR (≤ 10 min) | off inside T1; own checkbox |
- **Guards (binding):**
  - Current Windows user only; no service, no other sessions.
  - No network code in `tasktor-capture` (checked in CI by an import / P/Invoke allow-list).
- **Storage:**
  - Raw records are AES-256-GCM per record, keys wrapped by DPAPI (user scope), the same pattern Recall uses with per-record keys **[verified: secondary]**.
  - The FTS5 index and embeddings live in a separate encrypted SQLite page store **[engine: winsqlite3 lacks SQLCipher; OQ-C22]**.
  - Raw is kept 7 days; the index is kept until the user deletes it, with a size cap (default 2 GB, oldest first).
- **Never recorded, even in T1:**
  - password and credential fields;
  - the secure desktop;
  - excluded apps and hosts;
  - incognito unless opted in;
  - other Windows users;
  - microphone, camera and audio **content** (only "is playing" metadata).
- **Statement of intent.** For personal use on your own machine with your consent. It records only the signed-in user who turned it on, and it cannot run hidden.

### Superseded or revised by this section
| Item | Was | Now |
|---|---|---|
| Never-recorded list (Event schema) | no keystrokes, pointer, screen text, URLs, command lines, LL hooks | applies to **T0 only**. T1 follows D-C22's never-recorded list |
| D-C2 | worker sees numbers only | still true for **T0 refits**. The T1 content worker reads raw text to build the index (in a Job Object, no network) |
| D-C7 | ≤ 2 MB extra in the agent | unchanged for the agent; T1 adds `tasktor-capture` (D-C21) |
| D-C8 | "no keyboard or mouse hooks, ever" | T0: still none. T1: hooks in `tasktor-capture` only |
| D-C14 | agent-desktop reads nothing beyond the address-bar host | agent-desktop stays dev-time only, and C's own UIA reader adopts its snapshot / ref shape |
| C2 Browser domain | hostname via UIA | T0: unchanged. T1: companion extension, full URL + page text |
| C3 retention | 30-day raw titles | T0 unchanged; T1 raw 7 days, index kept |
| C5.6 embeddings | gated, later | the primary T1 representation, behind gate G8 |

### New open questions
| # | Question | Proposed |
|---|---|---|
| OQ-C20 | Default for `human`-only time in workflow totals? | yes; agent time shown as its own line |
| OQ-C21 | Embedding model (size vs quality on CPU) | MiniLM-L6-class ONNX, 384-d; measured in L1 |
| OQ-C22 | Encrypted index engine. Probe: `winsqlite3` has FTS5 + trigram but no codec | bundle SQLCipher for `index.db` in the capture process + content worker only (Technical design §9) |
| OQ-C24 | MSIX packaging (unlocks Windows.Media.Ocr + AppContentIndexer) | not for v1 |
| OQ-C25 | Store full URLs of excluded-from-text hosts? | hostname only |
| OQ-C26 | Track 2 threshold (25% screen / audio ≥ 60 s) | as proposed, tuned in L1 |

### Deep capture: technical design (T1; same depth as A's D-A5 and B's D-B11; design, not code)

**Probes for T1** (2026-11-04, local, non-admin, read-only; only counts, flags and timings were printed).
| Fact | Result | Consequence |
|---|---|---|
| `winsqlite3.dll` features | 3.53.4. `ENABLE_FTS5` = 1, and both `unicode61` and `trigram` FTS5 tables create. `HAS_CODEC` = 0 (no encryption). `RTREE` = 1 **[verified: local probe]** | Full-text search needs no bundled engine. Encryption at rest cannot come from the engine (OQ-C22). |
| SMTC from an unpackaged desktop process | `GlobalSystemMediaTransportControlsSessionManager.RequestAsync` OK in **170 ms**; 1 session, playback status readable **[verified: local probe]** | C20 now-playing works without MSIX. |
| PSReadLine history | file exists, `SaveIncrementally` (each command is appended as it runs) **[verified: local probe]** | C22 can tail it for PowerShell commands. |
| Windows Terminal shell integration | `autoMarkPrompts` not set **[verified: local probe]** | OSC 133 marks are opt-in. C22 must work without them. |
| Command lines of own processes | 44 of 44 readable without admin, but `Win32_Process` (CIM) took **1,162 ms** **[verified: local probe]** | CIM is too slow for per-event use. Use `NtQueryInformationProcess(ProcessCommandLineInformation)` per PID **[unverified: limited-access right suffices, A1]**. |
| `LowLevelHooksTimeout` override | not set, so the OS default applies **[verified: local probe]** | Keep the hook callback well under any default (D-C19). |
| Chrome page via UIA | browser chrome only (90 elements), no `Document` **[verified: local probe]** | Browser = extension (D-C18). Electron apps: OQ-C27. |

#### 1. Process topology
```
 tasktor-agent (always on, D-A3)            tasktor-capture (T1 only; owns tray indicator + edge line)
 ├─ logic thread  ◄──── named pipe ─────────┤  hook thread     WH_KEYBOARD_LL + WH_MOUSE_LL → InputRing
 │   (ActivityDigest events in,             │  uia thread      MTA, one UIA event handler group, cached walks
 │    Module control out)                   │  win thread      WinEvent foreground/location → VisibleWindows
 │                                          │  media thread    SMTC + WASAPI session events
 │                                          │  logic thread    KeyRunBuilder, ActorAttributor, SnapshotDiffer,
 │                                          │                  Redactor, ActivityDocumentBuilder (no locks)
 │                                          │  vault writer    RawVault appends (AES-GCM), fsync policy
 │                                          ├─ named pipe ◄── tasktor-nmh.exe (one per browser profile; started by Chrome)
 │                                          │                      ▲ stdio JSON (native messaging)
 │                                          │                 companion extension (MV3)
 └─ StartWorker ─► content worker (Python, batch, Job Object: OCR, chunk, embed, FTS merge) ◄─ reads vault via a
                                                                                          one-shot key handle
```
- **Lifetime.**
  - The agent starts `tasktor-capture` with `IProcessControl.StartWorker` in a **kill-on-close Job Object** with no CPU cap, so capture can never outlive the agent ("cannot run hidden").
  - Capture refuses to start without a valid consent record, and creates the tray icon before it installs any hook.
  - If it cannot show the indicator (for example, Explorer is not running), it exits.
- **Pipe.**
  - `\\.\pipe\tasktor-capture-<user SID hash>`. The DACL grants only the current user SID, with `PIPE_REJECT_REMOTE_CLIENTS`.
  - Frames are length-prefixed: a 4-byte length, a 1-byte type, then a MessagePack body.
  - Message types are listed in §11.
  - The agent never receives raw text. It gets **digests**: activity id, actor shares, typed char count, view count, media track, pointer stats. Text stays in the capture process and the vault.
- **Why `tasktor-nmh.exe` is separate.** Chrome starts the native-messaging host itself, once per connection, so it cannot be the long-lived capture process. The host is a ~1 MB Native AOT relay: stdio ↔ pipe. It checks that the calling extension origin is in `allowed_origins`.
- **Assemblies.** No new assembly in A's sense. `Tasktor.Capture` is a **host** in A's Hosts layer, like `Tasktor.Agent`. Its records go in `Tasktor.Contracts`, platform-free logic in `Tasktor.Core`, and Win32 / UIA / WGC / SMTC code in `Tasktor.Windows`. `tasktor-nmh` is a second tiny host.

#### 2. Volumes and rates (one heavy workday, reference PC) **[estimate, A1 measures]**
| Stream | Raw rate | After shaping | Stored per day |
|---|---|---|---|
| Key events | 15–40k down/up pairs | ~1.5–3k key runs | 30–120 KB text + metadata |
| Mouse moves | up to 125–1000 Hz while moving | 10 Hz, only when moved ≥ 8 px | ~2–5 MB → **features only after 7 days** |
| Clicks / scrolls | 1–3k / 2–6k | + element role / name at click | ~300 KB |
| UIA events (native apps) | bursts of 100s/s in busy apps | ≤ 1 walk/s/window sustained (token bucket) | diffs: 1–4 MB |
| Browser (extension) | page loads 200–600, mutation batches | ≤ 1 diff / 2 s / tab, only visible tab | diffs: 2–8 MB |
| Media | a few events/min | changes only | < 50 KB |
| OCR frames (T2) | 0 for text apps | ≤ 1 / 30 s / opaque window | frames deleted after OCR; text ~100 KB |
| **Raw vault** | | | **≈ 10–20 MB/day → ≤ 150 MB for 7 days** |
| **Kept index** (after dedup) | | | **≈ 1–3 MB/day FTS + 0.3–1 MB/day int8 embeddings ≈ 0.5–1.5 GB/year** (cap 2 GB, D-C22) |

#### 3. Key records (Contracts, T1)
| Type | Fields | Notes |
|---|---|---|
| `RawInput` (struct, 24 B) | `Qpc` (long), `Kind` (byte: KeyDown, KeyUp, Move, Button, Wheel), `Flags` (uint: injected bits as given), `A` (uint: vk or x), `B` (uint: scan or y), `Extra` (uint: wheel delta / button / time) | the hook writes **only this**; no strings or allocation in the callback |
| `Actor` | `Kind` (Human, Agent, Cli, InjectedUnknown, System), `Who` (AppId?), `Evidence` (bitmask: HwFlag, InjFlag, LowIl, AgentCpu, AgentChild, ShellEnter, PsHistory) | evidence bits are kept so the label is explainable (`LearnedValue`-style "why") |
| `KeyRun` | `Id`, `View`, `ElementRef`, `Start`, `End`, `Text` (string?, null if suppressed), `Keys`, `Backspaces`, `Chords` (≤ 16), `Submit` (Enter / Tab / focus-out / pause), `Actor`, `Suppressed` (None, Password, Excluded, SecureField, Sensitive) | `Text` passes through `Redactor` before leaving the logic thread |
| `ViewKey` | `Window` (WindowKey), `RootRef` (ulong) | a tab, a document or a panel; browser views = (window, tabId) |
| `SnapLine` (struct) | `Ref` (ulong), `Depth` (byte), `Role` (byte), `NameId` (int), `ValueId` (int) | strings are interned per view in an arena |
| `ViewDiff` | `View`, `At`, `Base` (snapshot seq), `Added` (lines), `Removed` (refs), `Source` (Uia, Extension, Ocr) | the unit stored in the vault |
| `PointerSample` | `At`, `X`, `Y` (physical px), `Monitor` | 10 Hz shaped |
| `ClickEvent` | `At`, `Button`, `X`, `Y`, `Target` (role + name, ≤ 80 chars, async-filled), `Actor` | |
| `MediaState` | `App`, `Title`, `Artist`, `Status`, `PositionMs`, `Audible` (bool, from the peak meter) | |
| `VisibleSet` | `At`, list of (`Window`, visible px², `IsForeground`) | recomputed on foreground / move-size end / minimize |
| `ActivityDigest` | `Activity`, `Actors` (ms per kind), `TypedChars`, `Views`, `Clicks`, `Scrolls`, `PathPx`, `Track` (1 = foreground, 2.. side) | the only T1 data that goes to the agent |
| `VaultRecord` header (40 B) | `Seq` (long), `Kind` (byte), `Day` (ushort), `AtUtcMs` (long), `Nonce` (12 B), `Len` (int), `Tag` (16 B, after the body) | see §8 |

#### 4. Core classes (state, operations, invariants, cost)
| Class | Owns | Operations | Invariants | Cost |
|---|---|---|---|---|
| `InputRing` | 65,536 × `RawInput` (1.5 MB, pinned), head / tail (SPSC) | `TryWrite` (hook) / `Drain(span)` (logic) | never blocks; when full, overwrite-oldest + `Dropped++` (reported) | O(1) |
| `LowLevelInputHook` (Windows) | hook handles, message loop, watchdog | `Install` / `Uninstall`; `Heartbeat()` | callback ≤ 50 µs p99.9; re-installs once per 30 s if silent while `GetLastInputInfo` advances; uninstalled before the indicator is removed | O(1) per event |
| `KeyDecoder` | per-layout state (dead key, shift, caps, AltGr) | `Decode(RawInput, hkl) → chars | chord` | **never calls `ToUnicodeEx` on the hook thread**; uses wFlags bit 2 (no keyboard-state change; Win10 1607+) on the logic thread, with its own key-state array **[per MS Learn ToUnicodeEx; A1 test with dead keys]** | O(1) |
| `FieldTruth` | last UIA value of the focused edit (≤ 8 KB) | `Diff(before, after) → inserted text` | for edits that expose `ValuePattern` / `TextPattern`, the **stored typed text is the value diff**, not the key decode (handles IME, autocomplete, paste, dictation); keys are the fallback | O(n) per change, n ≤ 8 KB |
| `UiaReader` (Windows; wrapped as `UiaWalkSource : IEventSource<UiaWalk>`) | `IUIAutomation6` client, one handler group, `CacheRequest`s, per-window token buckets | `Walk(element, scope) → SnapLine[]`; events → `ViewDirty(view)` | `TransactionTimeout` = 1 s (`IUIAutomation2`); skips `IsHungAppWindow` windows; depth ≤ 12, ≤ 400 elements per walk; never reads `Value` where `IsPassword` | ~5–300 ms per walk, off-loop |
| `RefMinter` | — | `Ref(path) = xxHash64(AutomationId ?? role+name, sibling index, parent ref)` | same element across walks → same ref (for diffs); collisions only cost a spurious ±line | O(1) |
| `SnapshotDiffer` | last snapshot per view (≤ 64 views live, LRU) | `Diff(view, lines) → ViewDiff?` | Myers O(ND) on (ref, text-hash). Re-baseline when diff > 60% of lines or every 50 diffs. Null when nothing changed | O((N+M)·D) |
| `MediaSessionSource` (Windows) | SMTC manager, session handlers, WASAPI session enumerator + meters | events → `MediaState` | meters polled at 1 Hz **only while** a session is active | ~0 idle |
| `VisibleWindows` (Windows) | z-ordered top-level list | `Recompute() → VisibleSet` | ignores cloaked (`DWMWA_CLOAKED`), tool and zero-area windows; ≤ 30 windows, rect subtraction | < 1 ms |
| `PixelFallback` (Windows) | WGC session per request, dHash per window | `MaybeCapture(window) → FrameRef?` | only T2 on, ≥ 10 s foreground, empty UIA text, Hamming > 6, ≥ 30 s since last; frame encrypted into the vault, never on disk plain | ~20–40 ms per capture **[estimate]** |
| `Redactor` (Core, shared with C3) | compiled detectors | `Scrub(text) → text, hits` | runs before **any** vault write; idempotent | O(n) |
| `ActivityDocumentBuilder` | open document per track | `Add(KeyRun | ViewDiff | ClickEvent | MediaState)`; `Close()` | ≤ 4 KB per document section; diff text compressed to "first seen" + new lines only | O(1) amortised |
| `TrackManager` | foreground track, side tracks | `On(VisibleSet | MediaState | fg)` | a side track opens at ≥ 25% screen **or** audible, ≥ 60 s (OQ-C26); closes after 30 s without either | O(windows) |
| `RawVault` | day files, day keys (wrapped), writer queue | `Append(record)`; `Read(range, purpose)`; `DropDay(day)`; `ForgetAll()` | per-record AES-256-GCM; nonce = day salt ‖ seq; a dropped day's key is zeroed and deleted **before** its file | ~1 GB/s AES-NI; I/O-bound |
| `ContentIndex` | `index.db` (encrypted, §9), embedding shards | `Ingest(documents)` (worker); `Search(q) → hits`; `Forget(range)` | a search reads only the index; snippets older than 7 days come from the stored chunk text, newer ones may also open the vault | BM25 + cosine, < 300 ms for 1 year **[estimate]** |

#### 5. Algorithms

**5.1 Hook callback (the only code that runs inside the OS's input path).**
```
LowLevelKeyboardProc(nCode, wParam, lParam):
    if nCode == HC_ACTION:
        k = (KBDLLHOOKSTRUCT*)lParam
        ring.TryWrite(RawInput{ Qpc=QPC(), Kind=down/up(wParam), Flags=k.flags, A=k.vkCode, B=k.scanCode, Extra=k.time })
    return CallNextHookEx(...)        // never swallow input; capture is passive
```
- No allocation, no locks, no UIA, no logging.
- The mouse proc is the same. Moves are written raw, and shaping (≥ 8 px and ≥ 100 ms) happens on the logic thread, so the callback stays branch-light.

**5.2 Actor attribution (D-C17 made exact).**
| Rule (first match wins) | Evidence | Actor |
|---|---|---|
| `LLKHF_INJECTED` clear | `HwFlag` | `Human` |
| injected, and the foreground window belongs to `osk.exe` / `TabTip` / an IME / a user-allow-listed remapper (AutoHotkey script the user marked "is me") | `InjFlag` + allow-list | `Human` (shown as "you, via tool") |
| injected, and exactly one known agent process had CPU > 0.5% **or** sent input to its own UIA connection in the last 2 s | `InjFlag` + `AgentCpu` | `Agent(app)` |
| injected, and the injector's integrity is lower (`LLKHF_LOWER_IL_INJECTED`) and a sandboxed agent is running | `LowIl` + `AgentCpu` | `Agent(app)` |
| injected, more than one candidate or none | `InjFlag` | `InjectedUnknown` |
- Process starts:
  - The parent chain is walked through B's process table (names-only scan + push exits), and the command line is read once per new PID only when its parent is a shell or a known agent.
  - Shell child within 2 s of an Enter-terminated `KeyRun` in that console's window → `Cli(shell)`.
  - Under a known agent → `Agent(app)`.
  - Otherwise → `System` / app.
- Short-lived commands that finish between scans are still captured as the **typed command** (C22). Commands typed by an agent are captured through the agent's own logs, if B reads them, or not at all; this is an honest gap.
- Why CPU and not window ownership: an agent driving the desktop injects into **other** apps' windows, so the foreground window says nothing about who typed.

**5.3 Typed text: two sources, one truth.**
1. Focus lands on an element. `UiaReader` caches `IsPassword`, `ControlType`, and the pattern availability for that element.
2. If the element is a password field, the run is suppressed, and the C3 exclude rules apply.
3. If the element exposes `ValuePattern` / `TextPattern` and its value is ≤ 8 KB, `FieldTruth` holds the value at focus. On run close (or `TextChanged`, debounced 500 ms) the inserted text = the diff of the new value against the held value. This is what gets stored.
4. Otherwise (games, terminals without text exposure, custom controls), the decoded key run is stored, with edits applied.
5. Either way: chords, key counts and timing come from the hook, so the actor and rhythm are always known.

**5.4 Snapshot → diff (D-C18 made exact).**
```
on ViewDirty(view) [debounced 750 ms, token bucket 5 burst / 1 per s per window]:
    lines = UiaReader.Walk(view.root, Subtree, cache=[Name, ControlType, Value*, IsPassword, IsOffscreen, AutomationId])
            filter: !IsOffscreen, role ∉ {decorative image, separator, scrollbar}, depth ≤ 12, count ≤ 400
            *Value only where !IsPassword and len ≤ 2 KB
    d = SnapshotDiffer.Diff(view, lines)          # Myers on (ref, hash(text))
    if d: Redactor.Scrub(d.text); vault.Append(d); ActivityDocumentBuilder.Add(d)
```
- Example: in a mail client, a new message opened in the reading pane yields ~20 added lines and ~20 removed ones. Scrolling a long list yields the newly visible items only. A clock tick in a status bar yields a 1-line diff that the **churn filter** drops: a ref whose text changed ≥ 10 times in 60 s is marked volatile, and only its first and last values per activity are kept.

**5.5 Browser extension.**
- `manifest.json` permissions: `tabs`, `nativeMessaging`, `storage`, `scripting`. Host permissions: `<all_urls>`. One content script at `document_idle`, all frames = false.
- Page text:
  - Use `main` / `article` / the largest text-dense block, plus the visible viewport text (TreeWalker over text nodes whose `getBoundingClientRect` intersects the viewport). Capped at 8 KB. Read on load + 2 s dwell.
  - After that, a `MutationObserver` (childList, characterData, subtree) sets a dirty flag. Every 2 s, while the tab is visible and the flag is set, viewport text is recomputed and line-diffed in the content script, and only the diff is sent.
  - **Never** read: `input` / `textarea` values (typed text comes from 5.3), `type=password`, any element with `autocomplete` in {`cc-*`, `one-time-code`, `current-password`, `new-password`}, and hosts on the exclude list (pushed from capture; matched before the content script does anything).
- Video: `<video>` play / pause / timeupdate (throttled to 10 s) → watching state for the tab, paired with SMTC (C20).
- Distribution: the public repo ships the unpacked extension; the user loads it in developer mode. A Web Store listing is a later question (OQ-C28).

**5.6 Media + audible.**
- SMTC `SessionsChanged`, `MediaPropertiesChanged`, `PlaybackInfoChanged` → `MediaState`. The source app is resolved through `AppIdentity` by AUMID.
- WASAPI `IAudioSessionManager2::RegisterSessionNotification` + per-session `IAudioMeterInformation::GetPeakValue` at 1 Hz while any session is active. Audible = peak > 0.01 for ≥ 3 of the last 5 s.
- Result: "YouTube in a background tab is playing and audible" without reading a pixel.

**5.7 Redactor detectors.**
| Kind | Rule | False-positive guard |
|---|---|---|
| card | 13–19 digits with optional spaces / dashes | Luhn checksum |
| IBAN | `[A-Z]{2}\d{2}[A-Z0-9]{11,30}` | mod-97 |
| US SSN | `\d{3}-\d{2}-\d{4}` | area ≠ 000/666/9xx |
| API key / token | known prefixes (`sk-`, `ghp_`, `gho_`, `xox[bp]-`, `AKIA`, `AIza`, `eyJ…` JWT 3-part) | prefix + length |
| high-entropy run | ≥ 32 chars of `[A-Za-z0-9+/=_-]`, Shannon entropy ≥ 4.0 bits/char | not inside a URL path that is the page's own URL |
| private key | `-----BEGIN … PRIVATE KEY-----` block | — |
| user rules | regexes from settings | — |
- Hits are counted per kind (guardrail: a sudden rise means a leak source; the card says "N secrets blanked today").

#### 6. Threading in `tasktor-capture` (D-B11 rule applied to the new process)
| Thread | Does | Talks to the logic thread by |
|---|---|---|
| hook | the two LL hooks + message loop; nothing else | `InputRing` (SPSC) |
| uia | handler group, walks, `FieldTruth` reads, click-target lookups | MPSC event queue |
| win | `SetWinEventHook` foreground / move-size end / minimize → `VisibleWindows` | MPSC queue |
| media | SMTC + WASAPI callbacks | MPSC queue |
| pipe I/O | agent pipe + nmh pipes (overlapped I/O) | MPSC queue |
| **logic** | decoding, runs, actor, diffs, redaction, documents, tracks, digests | — (owns all state, no locks) |
| vault writer | AES-GCM + append + flush every 5 s or 256 KB | bounded queue; backpressure → drop mouse samples first, never key runs |
- Ring drained every 100 ms, or at once when ≥ 50% full.

#### 7. Memory layout of `tasktor-capture` (budget ≤ 40 MB private)
| Item | Size |
|---|---|
| .NET AOT runtime + code | ~12–15 MB **[A0 measured for the agent; same runtime]** |
| `InputRing` | 1.5 MB |
| UIA client (COM, caches) | ~5–10 MB **[estimate]** |
| Snapshots: 64 views × ≤ 400 lines × 16 B + interned text ≤ 32 KB | ~2.5 MB |
| Open documents, runs, tracks | < 0.5 MB |
| Vault write queue (≤ 256 KB × 2) | 0.5 MB |
| WinRT media | ~2 MB |
| **Total** | **≈ 25–32 MB** |

#### 8. Raw vault format and keys
- **Record:** 40 B header (§3) ‖ ciphertext ‖ 16 B tag. AES-256-GCM, AAD = the header.
- **Keys:**
  - Nonce = 4-byte day salt ‖ 8-byte seq. The sequence number never repeats within a day key.
- **Retention:**
  - Crypto-erase is the real guarantee: without the day key, the file is noise.
- **Forget a time range:** delete vault rows for the range, and delete index rows by `activity_id` range.
- **Content worker access:** the agent passes the day DEKs for the requested range through the worker's stdin (never on the command line or in env). The worker zeroes them on exit, and the Job Object kills it on agent exit.

#### 9. Content index
- **Engine:** SQLite with FTS5. `winsqlite3` has FTS5 and trigram (probe) but **no codec**, so either:
  - **(a)  recommended:** the capture process and content worker bundle **SQLCipher** (BSD-style license; `SQLitePCLRaw.bundle_e_sqlcipher` for C#, `sqlcipher3` wheel for Python) for `index.db` only. The key is a separate DEK under the same KEK. The agent keeps `winsqlite3` for T0.
  - **(b)** `winsqlite3` with a **contentless** FTS5 table (`content=''`), plus chunk text in an app-level AES-GCM column. The index b-tree still holds plain terms on disk, so this is weaker.
  - → OQ-C22 now has evidence. Recommend (a).
- **Tables:**
  - `chunk(id, activity_id, track, day, kind, app, host, actor_mask, start_ms, end_ms, text_enc?, simhash)`
  - `fts USING fts5(text, app, host, tokenize='unicode61 remove_diacritics 2')`
  - `fts_tri USING fts5(text, tokenize='trigram')` (identifiers, code, partial words)
  - `link(chunk_id, kind, target)` (URL / file / window AUMID for reopen)
- **Chunking:** an activity document is split at section boundaries into 1–2 KB chunks with 15% overlap. Near-duplicates (SimHash-64 Hamming ≤ 3 against the last 500 chunks of the same app) are dropped, which is most of screen text **[estimate 5–10×, L1]**.
- **Embeddings:**
  - Local encoder (OQ-C21), 384-d, L2-normalised, int8 per-dimension quantised.
  - Shards `emb\YYYY-MM.i8` + `emb\YYYY-MM.ids`, encrypted at rest with the index DEK and decrypted into memory only during a search.
  - Search = brute-force dot product (≈ 1M × 384 B = 384 MB/year at the cap → only shards in the query's date range are loaded; default the last 90 days ≈ 100 MB, ~40 ms AVX2 **[estimate]**).
- **Search:** BM25 (FTS5) top 100 ∪ cosine top 100 → reciprocal-rank fusion (k = 60) → group by activity → snippet + "reopen" link + actor badge.
- **Workflow learning input:** each activity's document embedding = the mean of its chunk embeddings (weighted by length). It goes into the T1 training extract next to D-C4's hashed features. The extract stays numbers-only (D-C2 holds for refits).

#### 10. Failure modes
| Failure | Detection | Behaviour |
|---|---|---|
| OS silently removed an LL hook (timeout) | watchdog: `GetLastInputInfo` advances, ring silent 30 s | re-install once; on a second loss within 10 min, key capture stops and the card says "keyboard capture stopped (system removed hook)" |
| UIA call hangs (hung app) | `TransactionTimeout` 1 s, `IsHungAppWindow` | skip the window for 60 s; its views keep their last snapshot |
| Elevated (admin) window in front | `OpenProcessToken` elevation check / UIA access denied | text not read (UIPI); key runs into it **[LL hook delivery to higher-IL foreground unverified, A1]**; marked "elevated: limited" |
| Extension disconnected / not installed | no hello for 60 s while a Chromium window is foreground | browser falls back to T0 (hostname via UIA address bar) and the card offers to install the extension |
| Vault disk low (< 2 GB free) | free-space check per flush | stop pixels, then mouse samples, then pause T1 with a notification; never delete user files |
| Antivirus quarantine / block | process exit code / hook install failure | card: "blocked by security software"; nothing retried in a loop (OQ-C23) |
| Sleep / resume, fast user switch, lock | WTS + power events (agent, via the pipe) | hooks stay; runs close at lock; another user's session is never captured (the process is per-session) |
| Secure desktop (UAC, Ctrl+Alt+Del) | the hook sees nothing | gap recorded as `secure_desktop` from WTS / desktop-switch events |
| Clock jump | QPC vs wall clock | records carry both; ordering by QPC (A's rule) |

#### 11. Pipe messages (capture ↔ agent ↔ nmh)
| Dir | Type | Body |
|---|---|---|
| capture → agent | `Digest` | `ActivityDigest` (no text) |
| capture → agent | `Health` | ring drops, hook reinstalls, walk p95, vault bytes, redaction hits, CPU / private MB |
| nmh → capture | `Ext.Hello` | browser, profile hash, extension version, incognito-allowed flag |
| nmh → capture | `Ext.Tab` | tabId, windowId, url, title, audible, incognito, event |
| nmh → capture | `Ext.Text` | tabId, seq, base, added lines, removed line ids |
| nmh → capture | `Ext.Media` | tabId, playing, positionS |

#### 12. Test seams (T1)
| Seam | Fake | Lets a test |
|---|---|---|
| `IEventSource<RawInput>` (the ring drained as events) | scripted key / mouse streams with chosen injected flags | runs, edits, dead keys, chords, actor rules, ring overflow |
| `IEventSource<UiaWalk>` (`UiaWalkSource` emits plain walk records; §13.2) | recorded walks / in-memory trees with password fields, volatile clocks, 10k-item lists | diff, churn filter, re-baseline, caps, password never read |
| `IEventSource<ExtMessage>` | recorded extension frames (synthetic pages) | tab views, diffs, incognito drop, excluded host |
| `IEventSource<MediaState>` | scripted | side tracks, YouTube + chess compound |
| `IClock` | fake | 7-day drop, 03:00 job, debounce, token buckets |
| `RawVault` over a temp dir | real crypto | tamper (bad tag), truncated tail, drop day, forget all, nonce uniqueness |
| `Redactor` | corpus of synthetic secrets + look-alikes | recall ≥ 0.99 on the corpus, false positives ≤ 1% on synthetic prose |

#### 13. How C builds on A and B: the type delta (D-A5 rule: reuse shapes and the seven interfaces first)

**13.1 C's data, by A's four shapes**
| Shape | T0 (core tracking) | T1 (deep capture) | A/B class that holds it |
|---|---|---|---|
| **Event** | `WindowEvent`, `PresenceEvent`, `DomainRead`, `ProcessExit` (B) | `RawInput`, `KeyRun`, `ClickEvent`, `ViewDiff`, `MediaState`, `ExtMessage`, `ActivityDigest` | `EventChannel<T>` + `Cooldown` / `HoldFor` (debounce, token buckets); stored by `EventStore` / `RawVault` |
| **Table** | windows (`KeyedTable<WindowKey, WindowRow>`), clusters, totals | views (`KeyedTable<ViewKey, ViewState>`), tracks, `VisibleSet`, extension connections | `KeyedTable<TKey,TRow>` with `Diff()`; `TopN` for "top workflows" |
| **Tree** | process parent chain (job / tool processes under the launching app) | process parent chain for actor rules; **the UIA snapshot** (flattened to `SnapLine` + depth) | `TreeRollup` (anchor as a constructor parameter: anchor = shell or agent for actor rules); the UIA tree is walked, not rolled up, so it needs no class from A |
| **Series** | active time per workflow, switch rate | pointer speed / click rate per activity, actor time shares, health (ring drops, walk p95) | `RingBuffer<T>`, `RateCounter`, `MetricFrame` (health as `MetricId` rows: `capture.walk_p95`, `capture.ring_drops`, …) |

**13.2 The seven interfaces: what C implements, and the only changes**
| Interface (A) | C's implementations | Change? |
|---|---|---|
| `ITableSource<K,R>` | reuses A's `ProcessTableSource` with a **names-only** lease (B's column-set amendment) | none |
| `IProcessControl` | B's members +  `StartWorker(WorkerSpec) → ProcKey`, `CancelWorker(ProcKey)` (T0 refit worker, T1 content worker **and** the `tasktor-capture` lifetime) +  `SendControl(ProcKey, ControlMessage)` (generalises B's `RequestPause`: a control message to Tasktor's own child over its pipe instead of a file) | **3 members** (project-level change, H review) |
| `INotifier` | "keyboard capture stopped", "blocked by security software", disk-low pause | none |
| `IClock` | every debounce, 7-day drop, refit cadence | none |
| `IMetricSource` | not used directly; capture health reaches the agent as `ActivityDigest` / `Health` events | none |
- **No eighth interface.** The draft's `IUiaTree` test seam is replaced by `UiaWalkSource : IEventSource<UiaWalk>`: the Windows class does the COM walk and emits plain data (`UiaWalk` = view + `SnapLine[]` + string arena); `SnapshotDiffer` in Core only ever sees that record, so tests feed recorded walks. Same move as A's rule (a): the interface sits where Windows code meets testable logic, and that seam is already `IEventSource`.

**13.3 Reused from B as-is**
| B type | C use |
|---|---|
| `ProcKey` | every process reference (worker, capture, agents, shells) |
| `ProcessExitEvents` | worker / capture exit → B5 row, restart policy, "capture stopped" card |
| `WorkerProgressSource` + `ProgressRow` (`{stage, pct, msg}`) | refit and content-worker progress rows in B5 |
| `LearnedValue` + `ValueSource` | every learned output: cluster assignment basis, refit interval, side-track threshold (OQ-C26) |
| `KindStats` | per-app UIA walk cost and churn (learned "this app is noisy" → longer debounce), per-workflow typical duration for C11 |
| `LearnedThreshold` + `use_fallback` / `relearn` | confidence floors and guardrails as ordinary `RuleEngine` rules |

**13.4 Generalised from B (one shared class instead of two near-copies)**
| B class | Becomes | Why |
|---|---|---|
| `KindSignature` | `KeyedHasher(domain)`: `"kind|"` for B, `"tok|"` for C tokens, `"ref|"` for UIA refs in exports | one key, one implementation, domain-separated |
| `GuardrailSet` | `GuardrailSet<TSubject>`: B's subject = job kind; C's = cluster, model version, side-track rule, redaction rate | P1–P6 are the same checks over different subjects |

**13.5 Genuinely new types, and the gap each fills**
| New | Shape | Gap in A/B |
|---|---|---|
| `EpisodeBuilder` (T0) | event → table | nothing in A/B turns a focus stream into segments / activities / windows |
| `FeatureExtractor`, `FrequentTerms`, `ExemplarAssigner`, `WorkflowModel`, `ModelLoader`, `CurrentWorkflow`, `RefitPlanner`, `ClusterFeedback` (T0) | — | the learned-clustering loop; B learns numbers, C learns a partition |
| `EventStore`, `EpisodeStore` (T0), `RawVault`, `ContentIndex` (T1) | persistence | A/B persist only `KindStats` bytes; C keeps history and searchable content |
| `InputRing` (T1) | event buffer | `EventChannel` allocates per event and is MPSC; the hook needs a fixed, allocation-free SPSC ring |
| `KeyDecoder`, `KeyRunBuilder`, `FieldTruth` (T1) | event → event | typed text from keys + field values |
| `ActorAttributor` (T1) | event × tree → label | who drove the input; uses B's process tree, adds the injected-flag evidence |
| `RefMinter`, `SnapshotDiffer` (T1) | tree → event | stable element ids and line diffs (agent-desktop / agent-browser shape) |
| `TrackManager`, `ActivityDocumentBuilder` (T1) | table + events → document | concurrent activities and the unstructured representation |
| `Redactor` (T0 + T1) | text filter | C3's title redaction, extended with secret detectors |
| `DeepCapture : Feature` (T1) | façade | the Module's on/off, consent and indicator ordering |

**13.6 Assemblies (A's four, plus one host)**
| Assembly | T1 additions |
|---|---|
| `Tasktor.Contracts` | `RawInput`, `Actor`, `KeyRun`, `ViewKey`, `SnapLine`, `UiaWalk`, `ViewDiff`, `PointerSample`, `ClickEvent`, `MediaState`, `VisibleSet`, `ExtMessage`, `ActivityDigest`, `ControlMessage`, `VaultRecordHeader`, pipe frame schema |
| `Tasktor.Core` | `InputRing`, `KeyDecoder`, `KeyRunBuilder`, `FieldTruth`, `ActorAttributor`, `RefMinter`, `SnapshotDiffer`, `TrackManager`, `ActivityDocumentBuilder`, `Redactor` (extended), `RawVault`, `ContentIndex` (query side), `DeepCapture` |
| Hosts |  `Tasktor.Capture` (Native AOT, composition root for T1),  `tasktor-nmh` (stdio ↔ pipe relay); Python `tasktor_worker` gains the `content` job (OCR, chunk, embed, FTS merge) |
| Tests | fakes: recorded `RawInput` streams, recorded `UiaWalk`s, recorded extension frames, scripted `MediaState`; vault and index on temp dirs with real crypto |

### Per-feature sections, T1 (C16–C22)

#### C16. Actor attribution
| Sub | Behaviour |
|---|---|
| C16.1 | Every `KeyRun`, `ClickEvent` and process start carries an `Actor` with evidence bits (§5.2) |
| C16.3 | A "This was me" / "This was <agent>" correction on any Flyout row adds a per-app rule (labels feed a guardrail, as with B's "It's fine") |
| C16.4 | Workflow totals count `Human` time; agent time is a separate line per workflow (OQ-C20) |
- **Acceptance:**
  1. Keys typed on the physical keyboard → 100% `Human`.
  2. `SendInput` from a test harness registered as an agent, with CPU active → ≥ 99% `Agent(test)`.
  3. The same harness, unregistered → 100% `InjectedUnknown`, never another agent's name.
  4. On-screen keyboard → `Human`.

#### C17. Key stream
| Sub | Behaviour |
|---|---|
| C17.1 | LL keyboard hook → `InputRing` → `KeyRunBuilder` (§5.1, §5.3) |
| C17.2 | Stored text = the UIA value diff where the field exposes it, else the decoded keys with edits applied |
| C17.3 | Suppression: password, secure-field markers from the extension, excluded apps / hosts, app-declared sensitivity; a suppressed run stores length + duration only |
| C17.4 | Redaction before the vault (§5.7) |
| C17.5 | Per-app "don't record typing" switch, one click from the Flyout row |
- **Acceptance:**
  1. A password field in a native test app, a browser login page and the Windows credential dialog → **zero** characters in the vault, verified by scanning the vault with the test key.
  2. US-International dead keys (`'` + `e` → `é`) are stored correctly and the target app still receives `é` (the hook does not break dead keys).
  3. Hook callback p99.9 ≤ 50 µs over 1 h of synthetic typing at 15 keys/s.
  4. No hook removal over 8 h with the system under load (Defender full scan + build).

#### C18. Screen text
| Sub | Behaviour |
|---|---|
| C18.1 | Native apps: UIA handler group → debounced cached walks → `SnapshotDiffer` (§5.4) |
| C18.2 | Browser: companion extension (§5.5); T0 fallback without it |
| C18.3 | Terminals: Windows Terminal / conhost text through UIA `TextPattern` visible ranges (the same path as documents) |
| C18.4 | OCR fallback (T2), only per D-C16 |
| C18.5 | Churn filter: volatile refs keep their first and last values only |
- **Acceptance:**
  1. A text editor, a mail client, a PDF reader, Windows Terminal and a Chromium tab with the extension: opening a new document produces a diff containing its visible text within 3 s.
  2. An idle screen with a ticking clock produces ≤ 2 records / min.
  3. UIA walk p95 ≤ 150 ms; the capture logic thread never waits on UIA.
  4. **Screenshots = 0** during a 1 h scripted session of text apps + browser + YouTube.

#### C19. Pointer and attention
| Sub | Behaviour |
|---|---|
| C19.1 | LL mouse hook → 10 Hz shaped path, clicks with the target role + name, wheel |
| C19.2 | `VisibleSet` on change; per-activity screen share for every visible window |
| C19.3 | Raw path kept 7 days; afterwards only per-activity features (path length, speed p50 / p90, click rate, scroll rate, dwell share) |
- **Acceptance:**
  1. Shaped samples ≤ 10/s.
  2. The click target is filled for ≥ 90% of clicks in UIA-exposing apps within 100 ms.
  3. No pointer jank: hook p99.9 ≤ 50 µs at 1000 Hz mouse input.

#### C20. Media and concurrent tracks
| Sub | Behaviour |
|---|---|
| C20.1 | SMTC now-playing + WASAPI audible (§5.6) |
| C20.2 | Extension `<video>` state for browser tabs |
| C20.3 | `TrackManager` opens side tracks (≥ 25% screen or audible, ≥ 60 s); each has its own activity document |
| C20.4 | Compound workflows = clusters that co-occur across tracks for ≥ 50% of a workflow window; shown as "Chess + YouTube" |
- **Acceptance:**
  1. A scripted YouTube-in-background-tab + chess-in-foreground session for 20 min → two tracks, with the right apps and hosts, and the card shows both.
  2. A muted autoplay video does not open a track.

#### C21. Content index and search
| Sub | Behaviour |
|---|---|
| C21.1 | The content worker ingests closed activity documents in batches (D-C21) |
| C21.2 | FTS5 (word + trigram) + int8 embeddings; RRF search (§9) |
| C21.4 | `forget` by range, app or host deletes vault and index rows together |
- **Acceptance:**
  1. A phrase seen on screen (synthetic page) is findable within 35 min by exact words and by a paraphrase (embedding).
  2. After `forget` of that range, both searches return nothing, and `vault.db` has no row decryptable for that range.
  3. Search p95 ≤ 300 ms over 90 days of synthetic data.

#### C22. CLI command log
| Sub | Behaviour |
|---|---|
| C22.1 | Typed commands = Enter-terminated `KeyRun`s in console / terminal windows, reconciled with PSReadLine history lines appended within 1 s (wins when present, since it has the final edited text) |
| C22.2 | Terminal output = C18.3 diffs |
| C22.3 | Optional: Windows Terminal shell integration (`autoMarkPrompts`, OSC 133) gives prompt / command / exit-code marks; Tasktor offers to turn it on, never edits settings itself (OQ-C29) |
| C22.4 | Agent-run commands: children of known agents from B's scan (`Agent(app)`, command line read once) |
- **Acceptance:**
  1. 20 PowerShell commands typed in Windows Terminal → 20 rows with the exact final text.
  2. A command containing a token is stored redacted.
  3. A command run by a registered test agent shows as `Agent(test)`.

#### T1 efficiency acceptance (D-C21)
1. Module off: no `tasktor-capture` process, and no hook in the process list.
2. Module on, idle desk 10 min: capture ≤ 0.1% CPU, ≤ 40 MB private.
3. Module on, 1 h scripted heavy use: capture ≤ 0.5% CPU average, ≤ 40 MB private; agent still meets D-A3; no input lag measurable by a 1000 Hz latency harness (Δ p99 ≤ 1 ms).
4. Content worker: ≤ 5 min per 30-min batch on the reference PC at 25% CPU cap.

#### New open questions (continued)
| # | Question | Proposed |
|---|---|---|
| OQ-C27 | Electron apps (VS Code, Slack, Discord) expose their content only when renderer accessibility is on, so turning it on has a memory cost. Should C turn it on, and how? | A1 probe: UIA activation + a 10 s wait; measure each app's memory delta, then decide per app |
| OQ-C28 | Extension distribution: unpacked only, or a Web Store listing | unpacked for v1 |
| OQ-C29 | Offer to turn on Windows Terminal shell integration | offer with a diff preview; user applies it |
| OQ-C30 | Clipboard: record paste **length** only, or the pasted text (it is what lands in the field anyway, via C17.2) | length + source app; the text arrives through the field value diff |
| OQ-C31 | Raw mouse path beyond 7 days | features only |

- `LowLevelInputHook` (ring producer)
- `KeyRunBuilder` (decode + edits + suppression)
- `ActorAttributor`
- `UiaSnapshotter` (cached walk → lines with stable refs)
- `SnapshotDiffer` (Myers line diff + re-baseline rule)
- `ExtensionHost` (native messaging)
- `MediaSessionSource`
- `ActivityDocumentBuilder`
- `Redactor`
- `RawVault` (per-record AES-GCM + DPAPI)

## What changed from the 2026-11-03 draft
| Area | 2026-11-03 draft | Now | Why |
|---|---|---|---|
| Stack | features, episodes and the live assigner in **Rust** | all online code in **C# Core**; Python only for fits | OQ12 (A): C# everywhere |
| Worker input | worker opened `episodes.db` read-only |  worker reads a **numbers-only training extract** written by the agent; it opens no Tasktor database | physical privacy boundary; reproducible fits (extract hash in the model); no SQLite schema coupling across languages |
| Hashing | salted feature hash |  **keyed hash with B's install key** (domain-separated), one `KeyedHasher` for B kinds and C tokens | D-B10 reuse; one key to protect and forget |
| Process start/stop | own 15 s polled diff |  **B's 60 s names-only scan** (D-B2) + B's push exits | zero extra cost; D-A3 |
| Window open/close | hooked `EVENT_OBJECT_SHOW/DESTROY` |  **not hooked by C** (global flood risk); recorded only when F's window source runs | D-A3; little value for clustering |
| Title-change hook | global `EVENT_OBJECT_NAMECHANGE`, filtered in the callback |  **scoped to the foreground process** (re-hooked on each foreground change) | NAMECHANGE fires for every UI object in every app |
| Idle | `GetLastInputInfo` every 5 s, always |  every 60 s while active with an **exact backdated** idle start; 5 s only while idle | same accuracy, 12× fewer wake-ups while working |
| Live assignment | every 30 s |  **event-driven** (activity change, debounced) + the 60 s tick | no new timer |
| Model file | loaded into memory |  **memory-mapped** sidecars (file-backed, not private bytes) | D-A3 counts private memory |
| Density step | HDBSCAN on ~100 dims |  HDBSCAN on a **16-dim** projection; LSA fitted on **df ≥ 3** buckets only | local probe: SVD 32.6 s → 0.3 s; HDBSCAN 3.9 s → 0.9 s at 5k (below) |
| Stable ids | catalog: "centroid matching" |  **member-overlap** Hungarian matching; centroids only as a tie-break | SVD bases differ between refits, so raw centroids are not comparable |
| Fallback | "unclassified" |  **fallback chain**: workflow → activity cluster → app, each level gated | D-B9 pattern; never confidently wrong |
| Feedback | rename / merge only |  adds **"That's wrong"** on the card: a label that feeds a per-cluster guardrail | D-B9 learns from labels (B's "It's fine") |
| Refit cadence | fixed triggers |  fixed triggers + **adaptive back-off** and a drift-triggered early refit | D-B9 pattern applied to cadence |
| C13 self-use counts | in C |  **move to H** (it is Tasktor's self-measurement, unrelated to tracking) | catalog fold |
| Store engine | unspecified SQLite |  **Windows' built-in `winsqlite3.dll`** (present on the reference PC, v3.53.4), no bundled engine | zero bundle size; [support status unverified, W0] |
Kept as they were: the three levels, the two physical stores, the never-recorded list, the three rule strengths, redaction, the frequent-term dictionary, `prereg.json`, gates G1–G7, the embeddable Python choice, C14 CSV/JSONL + Python API, C15's contract.

## Reference-PC probes (2026-11-04, non-admin, read-only; no title, URL or path was printed or stored)
| Probe | Result | Used by |
|---|---|---|
| Alt-Tab-eligible windows (visible, unowned, not tool window, not cloaked) via `EnumWindows` + `GetWindowText` + `DwmGetWindowAttribute(DWMWA_CLOAKED)` | 6 windows, all with a readable title, 5 distinct exes; enumeration 23 ms; title length median 20 chars (max 53), median 3 tokens per title; 16 distinct tokens across all of them **[verified: local probe]** | C1, C5 (titles are short: the text block is sparse, so app + context blocks matter) |
| Redaction shapes in current titles | 0 titles with a 4+ digit run, 0 with an email shape, 0 with a drive path **[verified: local probe]** (a snapshot, not a distribution) | C3 |
| `GetLastInputInfo` from a standard user | readable; idle 31 ms at probe time **[verified: local probe]** | C1.2 |
| UIA address bar in a Chromium window (managed `System.Windows.Automation` client from PowerShell) | Edit element found, `ValuePattern` available, value parses as a hostname; **first find 661 ms** (cold), **second find 24 ms** (warm) **[verified: local probe]** | C2: never on the logic thread; cache the element per window; the old "200 ms timeout" would fail every cold read |
| `winsqlite3.dll` in System32 | present, file version 3.53.4, 1,154 KB **[verified: local probe]** | D-C9 store engine |
| Python on this PC | 3.14.0; numpy 2.4.3, scipy 1.17.1, scikit-learn 1.8.0 (has `sklearn.cluster.HDBSCAN`), onnxruntime 1.28.0; standalone `hdbscan` not installed **[verified: local probe]** | worker library choice (sklearn fallback is real) |
| `sklearn.utils.murmurhash3_32` and stdlib `hmac` + `hashlib.sha256` | both importable **[verified: local probe]** | the Python reference for `KeyedHasher` parity |
| **Synthetic refit, 5,000 activities** (15 planted topics, 2 BLAS threads, BelowNormal, machine busy with other sessions) | hashed TF-IDF over 2^18 buckets → LSA 64: **32.6 s**; with a df ≥ 3 column filter first (911 columns kept): **0.30 s**. k-means 0.7 s. HDBSCAN (sklearn) on 64 dims 5.6 s. 20 bootstrap HDBSCANs on 64 dims 69 s. Peak working set 303 MB, peak private 427 MB **[verified: local probe, synthetic data]** | C5 reduction, C8 limits, D-C7 |
| HDBSCAN cost by dimension (sklearn, synthetic Gaussian clusters) | 5k: 3.9 s at 64 dims vs **0.9 s at 16 dims**; 10k: 12.2 s vs **4.9 s** **[verified: local probe]** |  16-dim density step; fit cap 10k |
The synthetic data is easy (ARI 1.0 for both k-means and HDBSCAN), so these runs measure **cost, not quality**. Quality is only measured on real episodes at L1.

## Prior art (folded where noted)
| Source | What it does | What C takes |
|---|---|---|
| ActivityWatch | Open-source time tracker: window watcher + AFK watcher (180 s default) + browser extension; buckets of events; no learning **[verified: ActivityWatch docs/forum, 2026-11-03 session]** | Folded: AFK default 3 min; explicit gap events. Not folded: one extension per browser |
| RescueTime / Timing (commercial) | Category rules by app and title/URL, mostly hand-written or crowd-sourced **[unverified, general knowledge]** | Contrast: C learns categories from your data; rules exist only as excludes |
| Hennig, `fpc::clusterboot` | Bootstrap Jaccard per cluster; ≤ 0.5 dissolved, 0.6–0.75 doubtful, ≥ 0.75 stable, ≥ 0.85 highly stable; stability is not validity **[verified: fpc docs, 2026-11-03]** | Folded: G3 |
| DBCV (Moulavi et al.) via `hdbscan.validity_index` | Density-based validity for arbitrary-shape clusters with noise **[verified: hdbscan docs, 2026-11-03]** | Folded: G2 |
| Topic-model word intrusion (Chang et al.) | Humans spot the odd item out to test coherence **[unverified citation details]** | Folded: C6.4 intruder check |
| c-TF-IDF (BERTopic) | Class-based TF-IDF: terms that are frequent in a cluster and rare elsewhere **[unverified: BERTopic docs not re-read]** | Folded: C7.1 names |
| **LDOS** + **Guardrails** (HotOS 2025) | Learned OS policies need fallbacks and property checks (P1–P6) with REPORT / REPLACE / RETRAIN / DEPRIORITIZE **[verified: B session, paper text]** | Folded: the whole adaptive stance (D-C5), as in B |
| **LAKE** (ASPLOS 2023) | ML must pay for itself; fall back to the cheap path when it does not **[source: B session]** | Folded: null model on every fit; P5 overhead checks |

## Subfeature outline (draft, 2026-11-04)
✓ = carried from the 2026-11-03 draft unchanged in substance;  = new or changed.

**C1. Passive event capture** (Module: asked on first open of the workflow card, never silently)
- C1.1 ✓ Foreground app + window title, debounced 1 s;  title hook scoped to the foreground process
- C1.2  Presence: idle (backdated exact start), lock / unlock, sleep / resume, media-idle ("watching", not away)
- C1.3  Process start / stop from B's 60 s names-only scan and B's push exits (no own diff)
- C1.5 ✓ Notifications: source app + time only → **limited** (MSIX build only, needs user consent);  off by default inside C1
- C1.6 ✓ Explicit gaps (`agent_stop`, `suspend`, `pause`, `excluded_*`) so missing data is never read as idle
- C1.7  Opt-in screen: what is recorded, what never is, the default excludes, in plain words; one button
**C2. Browser domain** (inside C1)
- C2.1 ✓ Hostname only, via UI Automation from the address bar, on foreground / title change of a browser window
- C2.2 ✓ Discarded while the address bar has focus; must parse as a host with a public suffix
- C2.3 ✓ Private-window detector per browser must pass an A1 probe, else that browser is app-only
- C2.4  Cost guardrail: element cached per window, reads off the logic thread; repeated timeouts or a measured browser CPU rise switch domain reads off for that browser (P5)
**C3. Privacy gate: pause, excludes, retention, encryption** (Core of C1)
- C3.1 ✓ Pause, persisted across restarts; tray toggle (G6)
- C3.2 ✓ Rule strengths `drop-title` / `exclude` / `ignore`; default list
- C3.3 ✓ Redaction, always on (emails, 4+ digit runs, ids, own user name, paths)
- C3.4 ✓ Retention: raw 30 days; `forget` by range or all, derived rows included
- C3.5 ✓ Optional field-level encryption of titles and domains (key wrapped by DPAPI)
- C3.6  "What Tasktor knows": counts, oldest raw event, dictionary size, models; hide a word; forget
**C4. Episodes**
- C4.1 ✓ Segment (one foreground run of one window), C4.2 ✓ Activity (same-app run, glances absorbed), C4.3 ✓ Workflow window (15 min of active time), C4.4  Session (between idles ≥ 5 min; the unit for motifs and C10)
- C4.5 ✓ Built online in C#, Python reference for parity; ≥ 20 hand-built fixture streams
**C5. Features and clustering** (bundled Python worker)
- C5.1 ✓ Activity features: hashed TF-IDF titles + `dom:` token, app, time, context, shape;  "a job was running" flag from B
- C5.2  Reduction: df ≥ 3 buckets → LSA 64 → blocks weighted → PCA 16 for the density step
- C5.3 ✓ Null model (group by app), k-means baseline, HDBSCAN main (noise = unclassified)
- C5.4  Workflow model: windows × activity-cluster dwell share; HDBSCAN live, NMF as a report-only comparison in v1
- C5.5 ✓ Motifs: n-grams (2–4) over activity clusters per session; PrefixSpan only if n-grams miss interleaved patterns
- C5.6 ✓ Embedding variant (later, gated): only if TF-IDF fails the gates and the embedding passes them on the same data
**C6. Label-free evaluation** (pre-registered)
- C6.1  G0 data gate; C6.2 ✓ G1–G7;  G6b "information beyond app"
- C6.3 ✓ `eval.json` per fit; shown in B5's refit row and the flyout's Model tab
- C6.4  Human check (folds the proposed "intruder check" row): intruder test, report only; optional self-label sample (the horizon card's test)
**C7. Naming, rename, merge**
- C7.1 ✓ Deterministic auto name from c-TF-IDF over frequent terms only
- C7.2 ✓ Overrides layer (`name`, `merge`, `hide`, `split-request`) that survives refits
- C7.3  **That's wrong** on the current-workflow card: a negative label per assignment → per-cluster guardrail
- C7.4  Hide a word: removes a term from names and the dictionary
**C8. Refit and stable ids**
- C8.1 ✓ Triggers (idle, AC, not in a meeting / event mode, enough new data);  C8.2 adaptive back-off + drift-triggered early refit
- C8.3 ✓ Limits: BelowNormal + EcoQoS, Job Object (25% CPU, 1.5 GB), 2 BLAS threads;  kill-on-close;  fit set capped at 10k activities
- C8.4 ✓ Stages `features → SVD → fits → bootstrap → eval → export`, checkpointed, streamed to B5 (B's contract, unchanged)
- C8.5  Stable ids by member-overlap matching (catalog said "centroid matching")
- C8.6 ✓ History: current model re-labels the past; closed weeks are frozen
- C8.7  Model swap: validate, atomic rename, keep 2 previous versions for rollback
**C9. Current workflow + time per workflow (X1)**: card at S/M/L, flyout Workflows tab, fallback chain, pre-gate progress card
**C10. Switches and interruptions**: per day, 14-day sparkline
**C11. Unusual episode** (later): private flag, never an alert
**C12. Next activity / workflow prediction** (later): ships only if it beats a first-order Markov baseline by ≥ 5 points
**C13. Self-use counts** →  **move to H** (H14, Core)
**C14. Export and Python API**: CSV / JSONL + read-only `tasktor` package; Parquet later
**C15. Custom models** (Module, later): `models/custom/<id>/`, same limits, live only through the built-in model format

**Catalog-shape proposals ()**
- C1.4 window open / close leaves C (F owns window lifecycle events); C1.5 notifications stays **limited** and off by default.
- C13 moves to H (H14 "Self-use counts", Core, A1).
- Proposed "intruder check" row folds into C6 (C6.4 Human check).
- C8 keeps its L2 phase; L1 runs the same worker by hand (`tasktor refit`).
- X1 and C9 are one feature (C9 is the spec, X1 the card name in the Frame).
- No cuts. Tiers unchanged: C1–C12, C14 Module (inside C1); C15 Module, later.

## Cross-cutting decisions
- ** D-C1 Three levels and a fallback chain.** *Segment* (one foreground run of one window; never clustered) → *activity* (a same-app run with short glances away absorbed; clustered into **activity clusters**) → *workflow window* (15 min of active time described by its mix of activity clusters; clustered into **workflows**, which span apps). The card shows the most specific level that passes its gate: **workflow → activity cluster → app**. The app level comes from `AppIdentity` and is always true, so C is never worse than "group by app" (A2) on day one.
- ** D-C2 Two stores and an extract.** `events.db` holds raw events for 30 days and is the only file with full titles. `episodes.db` holds segments, activities, windows, hashed token weights, frequent terms, assignments and cluster history, and is kept. **The Python refit worker opens neither** (T1's content worker is the one exception, D-C22): before each refit the agent writes a numbers-only **training extract** (hashed bucket ids, weights, dense features, day indices, app ids). The C14 Python API and C15 custom models open `episodes.db` read-only; nothing outside the agent ever opens `events.db`.
- ** D-C3 The learning split.** Online, in **C# Core**: capture, the privacy gate, episode building, tokenizing and hashing, feature vectors, online assignment, time totals, switch counts, guardrail metrics. Batch, in the **Python worker**: IDF, LSA, PCA, k-means, HDBSCAN, bootstrap, gates, c-TF-IDF naming, id matching, motifs, export. Python carries a **reference implementation** of the online parts, used only in parity tests. Reason: LDOS / LAKE's overhead lesson (B's P5): the always-on part must be cheap and exact; the expensive part runs rarely, capped, and exits.
- ** D-C4 Long-term text = keyed hashes + a frequent-term dictionary.** Activities keep token buckets `HMAC-SHA256(install_key, "tok|" + token)` → first 4 bytes mod 2^18, with B's install key (D-B10) under a separate domain prefix. A readable term survives past 30 days only if it occurred in ≥ 5 activities on ≥ 3 distinct days; those terms make cluster names. Rare words (names, ticket numbers, one-off subjects) expire with the raw events. Honest limit: a keyed hash is not encryption; anyone holding the key and the file can test guesses. The key is DPAPI-protected for the user (B's OQ-B15).
- ** D-C5 One learning framework with D-B9.** Every learned output in C is an adaptive policy in B's shape: learned answer, fixed fallback, trust ramp, guardrails (P1–P6 → REPORT / REPLACE / RELEARN / DEPRIORITIZE), labels, and an explanation (`LearnedValue`). Full mapping in Architecture → "One learning framework". Includes a **null model on every fit**: if clusters only rebuild app groups (G6 / G6b fail), C shows apps.
- ** D-C6 Pre-registration is a file.** `prereg.json` ships in the repo with every threshold in this spec. Its SHA-256 is stamped into each model and `eval.json`. Changing a threshold means a version bump and a written reason **before** the next fit. On the user's install, the first fit on real data happens only after the file is committed.
- ** D-C8 Capture is event-driven and scoped.** Foreground and minimize events are hooked globally; title changes only for the current foreground process (re-hooked on each foreground change); no `SHOW` / `DESTROY` / `LOCATIONCHANGE` hooks; idle start is backdated exactly from `GetLastInputInfo`, so a 60 s poll loses no accuracy; process start / stop comes from B's existing scan. No keyboard or mouse hooks in T0 (T1 deep capture: hooks live only in `tasktor-capture`, D-C19 / D-C21).
- ** D-C9 Store engine and threading.** SQLite in WAL mode through Windows' own `winsqlite3.dll` (no bundled engine) **[support status as a public API unverified, W0]**; fallback: the `e_sqlite3` build that ships with `Microsoft.Data.Sqlite`. The store classes are concrete Core classes, not an interface: SQLite runs the same way on Linux CI with an in-memory database, so it is not a seam by A's definition (D-A5 §3). One **store writer thread** owns the write connection; the logic thread enqueues batches (D-B11: actions off the loop). Commits every 10 s or 64 events, and at idle, lock, suspend and agent stop, so a crash loses at most 10 s.
- ** D-C10 The model is a file contract, applied online in C#.** `models/<ver>/model.json` + little-endian `float32` / `int32` sidecars, memory-mapped read-only by the agent. Assignment runs when the open activity changes (debounced 5 s) and on the 60 s tick; confidence floor 0.5; the shown workflow changes only after 2 agreeing assignments. Details in Architecture → "Model contract and online assignment".
- ** D-C11 The Python worker.** python.org embeddable Python 3.13 + vendored wheels pinned by a lockfile, not PyInstaller. One process per job, launched suspended into a kill-on-close Job Object (25% CPU, 1.5 GB), BelowNormal + EcoQoS, 2 BLAS threads; job spec on stdin, B5's `{stage, pct, msg}` lines on stdout, results in `models/staging/`. Never resident. Details in Architecture → "The Python worker".
- ** D-C12 Stable ids by member overlap.** New clusters match old ones by Hungarian assignment on member Jaccard (activities in both fit windows), threshold 0.5; centroid cosine in the **new** model's space (old members re-projected) breaks ties. Ids are integers, never reused.
- ** D-C13 Type design.** No new interface, no new block kind, no new assembly. New sources implement existing interfaces; `IProcessControl` gains 2 members for the worker; B's `GuardrailSet` and `KindSignature` generalise into shared classes. Full list in Architecture → "New classes, and why each is a real gap".
- ** D-C14 agent-desktop is a reference and a dev-time probe, not a dependency.** See Architecture → "agent-desktop, read side".
- ** D-C15 Tiers and defaults.** Tracking stays a **Module, off** until the user turns it on from the workflow card (Frame privacy rule 3). Once it is on, learning is on (it is the point of the module), consistent with D-B9's "learning on by default". Turning tracking off removes the hook and closes the stores, so it costs nothing (D-C7).

###  D-C7 Efficiency: C must fit inside D-A3
**Where C could spend memory, CPU or disk, and the design that bounds it** (sizes are estimates until A1 measures them **[unverified]** unless marked)
| Part | Risk | Design |
|---|---|---|
| Title-change events | `EVENT_OBJECT_NAMECHANGE` fires for every named UI object in every process; out-of-context delivery marshals each one to the agent | hook it with `idProcess` = the foreground process only (`SetWinEventHook` takes a process filter **[verified: SetWinEventHook doc, 2026-11-03 session]**), re-hooked on each foreground change; in the callback keep only `OBJID_WINDOW` / `CHILDID_SELF` of the foreground `hwnd`; coalesce over 1 s |
| Other window events | `SHOW`, `DESTROY`, `LOCATIONCHANGE` are global floods | not hooked by C |
| Idle polling | 5 s polls = 12 wake-ups a minute forever | 60 s poll while active: `GetLastInputInfo` returns the time of the last input, so idle start is **backdated exactly**; 5 s polls only while the user is already idle (the PC is not in use) |
| Title reads | `WM_GETTEXT` to a hung window blocks the caller | `GetWindowText`, which for another process's window does not send a message **[unverified doc detail, A1]**; called once per debounced change |
| Browser domain | cold UIA find measured at 661 ms; Chromium may build its accessibility tree for an attached client | dedicated UIA thread; element cached per window (warm find 24 ms **[verified: local probe]**); read only on title change of a browser window; per-browser cost guardrail (C2.4) |
| Raw store writes | an fsync per event | WAL, `synchronous=NORMAL`, batched commits (10 s / 64 events); one writer thread |
| SQLite in the agent | engine and page cache in private memory | system `winsqlite3.dll` (image pages are shared, not private); `cache_size` 256 KB per connection; one write connection; reads for cards come from in-memory totals, not queries |
| Open episode | unbounded token lists | the open activity keeps ≤ 256 distinct tokens with dwell weights (`TopN` keeps the heaviest); closed activities are flushed and dropped |
| Model in the agent | an LSA matrix of up to ~10k × 64 floats (≈ 2.5 MB) | **memory-mapped** read-only sidecars: file-backed pages, not private bytes, trimmable by Windows; one assignment touches ≈ nnz rows (≈ 20 × 256 B) **[design; private-bytes accounting of mapped views to confirm, A1]** |
| Assignment | a timer per activity | event-driven (activity change, debounced 5 s) + the existing 60 s tick; O(nnz·64 + 100·16 + E·16), microseconds |
| Totals for cards | queries per viewer refresh | today / week totals kept in memory per cluster (≈ 40 × 3 numbers), rebuilt from `episodes.db` at start |
| Worker | Python + numpy + scipy + scikit-learn are large | **never resident**; only during a refit; capped by a Job Object (1.5 GB, 25% CPU), killed if the agent exits (kill-on-close) |
| Refit cost | grows super-linearly with activities | fit set capped at 10k (stratified by day); density step at 16 dims; bootstrap at 16 dims. Measured on synthetic data: HDBSCAN 0.9 s (5k) / 4.9 s (10k) at 16 dims **[verified: local probe]** |
| Disk: raw | grows with use | 30-day retention, nightly purge by an indexed timestamp, weekly `VACUUM` at idle. ≈ 5–10k rows/day × ~120 B ≈ 1 MB/day → ≈ 30 MB **[estimate]** |
| Disk: derived | kept forever | ≈ 300 activities/day × (row + ~30 buckets × 8 B) ≈ 100 KB/day ≈ 36 MB/year **[estimate]**; models ≈ 1–5 MB each, 3 kept |
| Disk: runtime | bundled Python | embeddable + stripped wheels ≈ 60–80 MB **[estimate, WS9]**; installed once, never loaded by the agent |

**Budgets (inside D-A3, not added to it)**
| State | C's share of agent private memory | C's share of agent CPU | Worker |
|---|---|---|---|
| Tracking off | 0 (no hook, no store open, no model mapped) | 0 | none |
| Tracking on, user away | ≤ 2 MB | 5 s idle polls + the tick; ≤ 0.01% | none |
| Tracking on, active use | ≤ 2 MB | ≤ 0.05% average **[unverified, A1]** | none |
| Viewer open | within A's 30 MB / 1% | card from in-memory totals | none |
| Refit running | + ≤ 0.2 MB (pipe, progress row) | reading worker stdout | ≤ 1.5 GB cap; 427 MB peak measured at 5k synthetic; ≤ 25% CPU |

**Acceptance**
1. Tracking off: no WinEvent hook installed, no `events.db` handle open, no model view mapped (handle / hook count test).
2. 8 h of normal use on the reference PC with tracking on: agent private memory within budget and grows by < 1 MB (manual + logged).
3. A scripted app that changes 1,000 UI element names per second in a background window adds no measurable agent CPU (hook-scope test); the same in the foreground window adds < 0.5% (debounce test).
4. Idle start recorded within ±1 s of the scripted last input while polling at 60 s; idle end within 5 s (fake clock + fake input source).
5. Killing the agent during a refit ends the worker within 2 s (kill-on-close test).
6. A refit on a 10k-activity extract finishes within 5 min on the reference PC under its limits, and the worker process is gone afterwards (manual, L1; time recorded as a baseline).
7. Mapping a 10k-bucket model adds ≤ 0.2 MB to agent private memory (A1 measurement).

## Architecture (builds on D-A4 / D-A5 / D-A6 and D-B9 / D-B11; no parallel classes)

### C's data by shape, mapped onto the shared core
| Shape | Data in C | Key | Shared class it uses |
|---|---|---|---|
| **Event** | foreground, title, minimize / restore, idle, lock, sleep, process start / stop, notification, pause, exclude, agent start / stop | type + time | `EventChannel<CaptureEvent>`, `HoldFor` (debounce), `Cooldown` |
| **Table** | windows seen this run (window key → app, process) | `WindowKey` | `KeyedTable<WindowKey, WindowRow>` |
| **Table** | apps | identity key from `AppIdentity` | `AppIdentity` (shared ids, A's hard dependency) |
| **Series** | time per cluster today / this week; switches per day | cluster id + day | `RingBuffer` (14-day switches sparkline), plain counters |
| **Table** | open activity's tokens | token string (in memory only) | `TopN` (≤ 256 heaviest) |
| **Table** | model clusters and their feedback counters | `ClusterId` | `KeyedTable<ClusterId, ClusterRow>` |
| **Tree** | none new; app attribution of a foreground window uses `AppIdentity` + `TreeRollup` (WebView2 / Electron helpers → host app) | — | `TreeRollup` (A2's anchors) |
Episodes (segment → activity → window → session) are a **sequence** built from events. They are not a fifth shape: each level is a closed record appended to a table, and the builder is a state machine over the event stream (as B's job state machine is).

### Slot map: where C plugs in (A's D-A6 slots)
| Slot | C uses it for |
|---|---|
| New behaviour | `WorkflowTracking : Feature, ICardProvider` (the C9 card and the snapshot `workflow` block) |
| New decision | rules in the rules file: C's guardrails (P1–P6) over model metrics, using B's `use_fallback` / `relearn` actions; refit triggers as a rule (`refit when idle ≥ 10 min and power = ac and …`) |
| New action | `IProcessControl.StartWorker` / `CancelWorker` (Tasktor's own child, no `GuardChain`; B5 already cancels through the agent's own handle) |
| New look | existing blocks only: `Metric`, `Bar`, `Table`, `Sparkline`, `Badge`, `Progress`, `Actions` |
| Outside code | C14 Python API (read-only `episodes.db`), C15 custom models, the training extract format |

### Where C sits
```
 Windows ─► Sources (Tasktor.Windows) ───────────────► Core (shared + C) ─────────────────────────► WorkflowTracking ─► Snapshot ─► Viewers
  WinEvent   WinEventSource [push, hook thread] ──┐                                                    (Feature,          workflow{}   S/M/L card,
  hooks      IdleSource [60 s / 5 s] ─────────────┤    PrivacyGate (pause → rule → private →            ICardProvider)                 flyout tabs,
  WTS        SessionSource [push] ────────────────┼──►  regex → redact → domain rule)                   │                              tray, CLI
  power      PowerEvents (A) [push] ──────────────┤        │                                            │
  UIA        UiaDomainSource [UIA thread] ────────┤        ▼                                            │
  Toolhelp   ProcessTableSource names-only (B) ───┤    EpisodeBuilder ─► segments / activities / windows │
  exits      ProcessExitEvents (B) [push] ────────┘        │                      │                     │
                                                           ▼                      ▼                     │
                                              StoreWriter thread ──► events.db   FeatureExtractor ─► ExemplarAssigner ─► CurrentWorkflow
                                              (batched)          └► episodes.db     (KeyedHasher)      (mmapped model)    (fallback chain,
                                                                        │                                    ▲            hysteresis)
                                                     TrainingExtract ◄──┘                                    │
                                                           │                                                 │
 Python worker (never resident) ◄── IProcessControl.StartWorker (Job Object) ── RefitPlanner (rules, guardrails)
   features → SVD → fits → bootstrap → eval → export ──► models/staging/<ver> ──► ModelLoader (validate, swap)
   stdout {stage, pct, msg} ──► B's WorkerProgressSource ──► B5 refit row
```
Frame mapping: sources + gate = C1.2; store = C1.7; online assignment = C1.6; worker = C2; card = C3 / X1. Scheduler: no lease of its own while active (event-driven; the 60 s tick is A's); a 5 s `IdleSource` lease only while the user is idle.

### Event schema (`events.db`, table `event`, append-only, WAL)
| Field | Type | Notes |
|---|---|---|
| `id` | int PK | |
| `ts_utc_ms` | int | wall clock |
| `tz_min` | int | UTC offset at capture (travel, DST) |
| `mono_ms` | int | monotonic ms since agent start; orders events across clock jumps |
| `kind` | small int enum | `fg`, `title`, `min`, `restore`, `idle_start`, `idle_end`, `lock`, `unlock`, `suspend`, `resume`, `proc_start`, `proc_stop`, `notif`, `excluded_start`, `excluded_end`, `pause`, `unpause`, `agent_start`, `agent_stop`; `win_open` / `win_close` only when F's source runs |
| `app` | int → `app` | `app` table: `AppIdentity` key (lower-cased exe basename, or package family / AUMID for packaged apps) + display name. **No path** (paths contain the user name) |
| `win` | int | window id from a per-install counter for each (hwnd, owning `ProcKey`) pair; survives hwnd reuse |
| `pid`, `pstart` | int | B's `ProcKey` (PID + creation time) |
| `title` | text, nullable | after the privacy gate and redaction (C3); null for `drop-title` apps; AES-GCM ciphertext when C3.5 is on |
| `domain` | text, nullable | hostname only (C2); encrypted with the title |
| `idle_kind` | small int, nullable | `input` / `media` (input stopped while the foreground app plays audio: watching, not away) |
Side table `term_candidate` (raw side, same 30-day life): token text, bucket, activity count, a 30-day presence bitmap. A candidate is promoted to `episodes.db` `term` when it reaches ≥ 5 activities on ≥ 3 days, and expires after 30 days without use.

**Never recorded in T0 core tracking (binding, published; T1 deep capture follows D-C22):** keystrokes or key counts, mouse positions or clicks, screen pixels, clipboard, field values, any UI Automation text other than the browser address bar's hostname, full URLs (path, query, fragment), file paths, process command lines (B reads them live for job rows; C never stores them), notification text, audio content. No low-level keyboard or mouse hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) and no raw-input sinks: idle comes from `GetLastInputInfo`, which returns only a time.

### Episode definition (built online by `EpisodeBuilder`; Python reference for parity)
| Level | Opens | Closes / splits | Kept |
|---|---|---|---|
| **Segment** | a window gains foreground (after the gate) | foreground change, idle start, lock, suspend, pause, exclude, agent stop | dwell ≥ 2 s; shorter flickers stay in raw but are ignored |
| **Activity** | the first segment of an app | another app holds foreground ≥ **30 s** (shorter visits are absorbed as "glances"); idle ≥ **5 min** (shorter idles stay inside and count toward `idle_ratio`); lock / suspend / pause; a **60 min** cap (marathons are chunked so one does not dominate) | app, start, end, active dwell, idle ms, glances, title changes, previous / next app, dwell-weighted token weights (hashed at close), `job_frac` (share of the activity with a B job running) |
| **Workflow window** | tumbling 15 min of **active** time | idle ≥ 5 min ends one early; windows with < 3 min active are dropped | dwell share per activity cluster, switches, glances |
| **Session** | first activity after an idle ≥ 5 min, lock or boot | the next such gap | ordered list of activity ids (motifs, C10) |
System UI is **ignore**, not an app: taskbar, Start, search, Alt-Tab, the Widgets board host, the lock screen and UAC's secure desktop never split a run and get zero dwell. Every threshold is in `prereg.json`; G7 refits with each one halved and doubled.

### Features (C#, per activity; the worker gets the numbers)
| Block | Content | Weight |
|---|---|---|
| Text | title tokens: lower-cased, split on non-letters / digits, app-name suffixes removed ("— Visual Studio Code"), stopwords and redaction tokens removed, 2–30 chars; plus `dom:<host>`; `KeyedHasher` → 2^18 buckets; dwell-weighted sublinear TF; × IDF from the model; L2-normalised; projected by LSA | 1.0 |
| App | one-hot over apps with ≥ 3 activities in the fit set, rest = `other` | 1.0 |
| Time | hour of day (sin, cos), weekend flag, log duration | 0.3 |
| Context | previous / next app one-hot (top apps) | 0.3 |
| Shape | idle ratio, glance count, title-change rate, `job_frac` ( from B) | 0.2 |
Cut from features (kept on the record): the app's CPU / RAM profile. It tracks what the machine does, not what the user does.
**Reduction ( revised):** keep buckets with df ≥ 3 in the fit set (911 of 2^18 on the 5k synthetic probe) → TF-IDF → TruncatedSVD to 64 → each block L2-normalised and weighted → concatenated (~100 dims) → PCA to **16** for HDBSCAN and the assigner. G7 also fits at 32 dims. Probe: the df filter took LSA from 32.6 s to 0.3 s; 16 dims made HDBSCAN 4.5× faster at 5k **[verified: local probe, synthetic]**.
**Embedding variant (later, gated):** only if TF-IDF fails the gates and a small sentence-embedding model (ONNX Runtime, BelowNormal) passes them on the same data. It is the one path that would need raw titles in the worker, so it would need its own privacy note and a separate opt-in.

### Clustering (Python worker)
- **Null model:** group by app (the app block alone). Every gate is also computed for it, and the learned model must beat it (G6b).
- **Baseline:** k-means, k from 5 to 40, chosen by silhouette on a 3k sample.
- **Main:** HDBSCAN, `min_cluster_size = max(8, 1% of n)`, `min_samples = 5`, `cluster_selection_method = eom`. Noise = **unclassified**, shown honestly, never forced into a cluster. Library: the standalone `hdbscan` package (BSD; `approximate_predict` for validating the C# assigner, `validity_index` for DBCV) **[verified: hdbscan docs and PyPI wheels to cp314, 2026-11-03 session]**; scikit-learn's `HDBSCAN` (present in 1.8.0 here **[verified: local probe]**) is the fallback, with DBCV and prediction implemented in the worker.
- **Workflows ( revised):** the matrix of windows × activity clusters (dwell share). v1 fits HDBSCAN on it (hard workflows, exemplar assigner, same code path as activities) and **NMF as a report-only comparison** in `eval.json` (soft: a window can be 70% "Tasktor" and 30% "email"). If NMF wins by the pre-registered rule (G3 and G4 both higher by ≥ 0.05 for two refits), a later version adds an NNLS assigner in C#.
- **Motifs:** n-grams (n = 2–4) over activity-cluster sequences within a session; kept if support ≥ 5 sessions on ≥ 3 days **and** lift ≥ 2 against a shuffled-within-day baseline. Own counter first; `prefixspan` (MIT, pure Python **[verified: PyPI, 2026-11-03]**) only if n-grams miss interleaved patterns.

### Label-free evaluation (pre-registered; feature view in C6)
| Gate | Metric | Threshold | Applies to |
|---|---|---|---|
| G0 Data | activities, distinct active days, active hours | ≥ 300, ≥ 7, ≥ 20 h (activities); ≥ 200 windows over ≥ 10 days (workflows) | before any fit |
| G1 Coverage | share of active dwell assigned (not noise) | ≥ 0.60 | model |
| G2 Separation | DBCV on a 3k sample | ≥ 0.20 | model |
| G3 Stability | mean bootstrap Jaccard, 20 resamples of 80% (Hennig) | ≥ 0.60 to show; ≥ 0.75 "stable" | each cluster |
| G4 Temporal | fit on odd days, assign even days with the **C#-equivalent reference assigner**, ARI vs full-fit labels | ≥ 0.50 | model |
| G5 Persistence | share of clusters matched to the previous fit (C8.5) | ≥ 0.80 | refits from the 2nd on |
| G6 Beyond app | ≥ 3 shown clusters split one app or join several; report AMI(clusters, app) | ≥ 3 clusters | model |
|  G6b Information beyond app | on held-out even days: mean cosine(activity text vector, its cluster centroid) − mean cosine(activity, its **app's** centroid) | ≥ 0.05 | model |
| G7 Sensitivity | ARI between the default fit and fits with each episode threshold halved / doubled, and at 32 dims | report only | model |
A model **activates** only when G0, G1, G2, G4, G6 and G6b pass; a cluster **shows** only with G3 ≥ 0.60. G6b is the null-model test in one number: if knowing the cluster says no more about an activity's words than knowing its app, the model adds nothing and C shows apps (D-C5).

### Naming and merging (feature view in C7)
- **Auto name:** `<top app> · <top 2 terms>`, terms ranked by c-TF-IDF (cluster vs all) and drawn **only from the frequent-term dictionary**. Workflows: their top 2 activity clusters ("Code + Chrome · tasktor"). Deterministic: same model → same names.
- **Overrides** (`overrides.json`, kept forever, keyed by stable ids): `name`, `merge` (id → id), `hide`, `split-request` (the next refit re-clusters only that cluster's members with `leaf` selection), `hide-term`.
- Overrides apply **after** a fit, so a refit never loses a rename; a merge is a view-level union until the next fit, which then matches the union as one id.

### Refit cadence and stable ids (feature view in C8)
- **Triggers (a rule):** at most once a day; idle ≥ 10 min; on AC; not in a meeting or event mode (I3, I13); and ≥ 50 new activities **or** ≥ 7 days since the last fit. `tasktor refit` forces one.
- ** Adaptive cadence (D-B9 shape):** learned interval = 7 days doubled after each refit whose G5 ≥ 0.95 with no new cluster, up to 28 days; any refit with G5 < 0.80 or a new cluster resets it to 7. **Early refit (guardrail P1):** if over the last 3 active days > 50% of active time is unassigned, request a refit now (still under the idle / AC conditions). Fallback = the fixed 7-day rule.
- **Fit window:** the last 90 days; capped at 10k activities by stratified sampling per day; activities outside the fit set are assigned, not fitted on.
- **Stable ids (D-C12):** Hungarian assignment (`scipy.optimize.linear_sum_assignment`) on member Jaccard; match if ≥ 0.5; ties broken by centroid cosine in the new space. Unmatched new → new id. Unmatched old → `retired` (name and history kept). Split → the larger child keeps the id, the other records `split_from`. Merge → the higher-overlap parent keeps the id, the other records `merged_into`.
- **History:** past activities are re-labelled by the current model so totals stay consistent; a week's totals are frozen when the week closes, so a refit cannot rewrite a week already shown.

### Model contract and online assignment (Frame C1.6)
**Files** (`models/<ver>/`, written by the worker into `models/staging/<ver>/`, validated and renamed by the agent)
| File | Content |
|---|---|
| `model.json` | schema version, model version, `prereg` hash, feature-spec version, extract hash, block weights, app and context vocabularies (identity keys), cluster table (id, name, status, stability, radius, exemplar range), workflow sub-model table, gate results summary, sidecar descriptors (name, dtype, shape, SHA-256) |
| `buckets.i32` | sorted kept bucket ids (k ≈ 1–10k) |
| `idf.f32` | IDF per kept bucket |
| `svd.f32` | k × 64 LSA components |
| `pca.f32` | (~100 + 1) × 16: mean + components |
| `exemplars.f32` | E × 16, E ≤ 20 per cluster (the highest-membership core points) |
| `wf_exemplars.f32` | workflow exemplars in window space |
| `eval.json` | every gate with its value and threshold; the null model's numbers next to the model's |
**Assignment (C#, `ExemplarAssigner`)**: tokens → `KeyedHasher` buckets → binary search in `buckets.i32` (unknown buckets dropped) → sublinear TF × IDF, L2 → × `svd` rows (nnz rows only) → L2 → concat weighted blocks → − mean, × `pca` → nearest exemplar per cluster → `confidence = clamp(1 − d / r_c, 0, 1)`, `r_c` = 95th percentile of member-to-nearest-exemplar distance. `d > r_c` → unassigned.
- **Floor:** 0.5 to show (pre-registered; changed only by a written amendment after L2, checked against G4).
- **Hysteresis:** the shown workflow / cluster changes only after 2 agreeing assignments.
- **When:** on activity change (title / foreground, debounced 5 s) and on the 60 s tick (duration feature). No timer of its own.
- **Parity:** the Python reference assigner and the C# assigner give identical ids and confidence within 1e-5 on the fixture set (CI on both sides).
- **Swap:** the agent checks schema, sidecar hashes, `prereg` hash, feature-spec version and that `eval.json` says the activation gates passed; then renames staging → `models/<ver>/` and swaps the mapped view on the logic thread. The previous 2 versions stay for rollback.

### The Python worker: bundle, launch, talk
**Bundle (WS9 input): python.org embeddable package + vendored wheels, not PyInstaller.**
- Embeddable zip + `Lib\site-packages` (numpy, scipy, scikit-learn, hdbscan, joblib, threadpoolctl) pinned by a lockfile; `python3X._pth` lists site-packages. Installed sizes measured earlier: numpy 31.7 MB, scipy 116.5 MB, scikit-learn 41.4 MB (≈ 190 MB with tests) **[verified: local measurement, 2026-11-03]**; stripping `tests/` and compressing should land at ≈ 60–80 MB **[estimate, WS9]**.
- Why not PyInstaller: freezing SciPy / scikit-learn needs hook upkeep **[community]**, and a frozen app cannot run user scripts that import the same libraries (C14, C15). One plain interpreter serves the worker, the Python API and custom models.
- **Python 3.13** (hdbscan wheels exist to 3.14; 3.13 leaves margin). `.pyc` precompiled at build; `PYTHONDONTWRITEBYTECODE=1` (the MSIX install folder is read-only).
**Launch (agent → worker)**
1. `RefitPlanner` decides a refit is due (rule) → the store writer writes the **training extract** to `train/<ver>/` (`meta.json`; `tokens.csr` = indptr / bucket / weight arrays; `dense.f32`; `days.i32`; `apps.i32`), with its SHA-256.
2. `IProcessControl.StartWorker(spec)`: `CreateProcess` of `python.exe -I -m tasktor_worker` **suspended**, with `BELOW_NORMAL_PRIORITY_CLASS`, assigned to a new Job Object (`CpuRate` 2500 = 25% of the machine, job memory 1.5 GB, `KILL_ON_JOB_CLOSE` **[flag semantics unverified, W0]**), EcoQoS via `SetProcessInformation` **[verified: SetProcessInformation doc, A3.4]**, environment `OMP/OPENBLAS/MKL_NUM_THREADS=2`, then resumed. `-I` = isolated mode (no `PYTHONPATH`, user site or current directory).
3. Job spec (JSON) on **stdin**: extract path, previous model path, overrides, `prereg.json`, output path, stage to resume from.
4. **stdout**: B5's JSON lines `{stage, pct, msg}`, stages `features → SVD → fits → bootstrap → eval → export` (B's contract, unchanged), read by B's `WorkerProgressSource`; stderr → `log/`. Each stage checkpoints to `train/<ver>/ckpt/` so B5's Pause (cancel + resume) loses at most one stage.
5. Exit via B's `ProcessExitEvents`; exit 0 + a staged model → `ModelLoader`. A watchdog ends the worker after 20 min **[estimate]**; two failures in a row → one toast (B5).
- **Developers:** setting `worker.python` points at any venv with the same lockfile; same entry point.
- **Isolation honesty:** the worker is Tasktor's own code from the install folder; it gets the extract, never a database. Custom models (C15) run in the same interpreter under the same Job Object but cannot be kept off the network without admin firewall rules (disclosed).

### agent-desktop, read side (D-C14)
agent-desktop (vercel-labs, Apache-2.0, v1.0.2 / a47ed664) is Rust: core + a Windows UI Automation adapter + CLI + FFI, with no event API (Frame; only the README has been read **[unverified beyond the README]**). With the stack in C#:
| Option | Verdict |
|---|---|
| Link its FFI library into the Native AOT agent | **no**: a second toolchain in the build, a Rust DLL in the always-on process, and C needs about three UIA calls |
| Port patterns (address-bar locate by control type + name, `ValuePattern` read, window identity) into C# via source-generated COM (the OQ12 spike already made a UIA call this way) | **yes**; copy no code without a NOTICE entry (Apache-2.0 §4) |
| Use its CLI / MCP **at development time** to snapshot each browser's accessibility tree and write the C2 per-browser fixtures (address bar locator, private-window marker) | **yes**: read-only, on the developer's machine, never shipped |
| Use it to read anything else in apps (document names, tab lists) | **no**: the never-recorded list allows only the address-bar hostname |

### C14 export and the Python API (feature view in C14)
- **Python API:** package `tasktor` in the repo and in the bundled runtime. `tasktor.open()` → read-only handle; `.activities(since=…)`, `.windows()`, `.features()` (scipy sparse + dense blocks, identical to the extract), `.clusters()`, `.assignments()`, `.model()` → reference assigner with `.assign(features)`. Lists of dicts, or DataFrames if pandas is installed. Schema versioned, documented in `docs/data.md`.
- Parquet later: pyarrow is not bundled; users with pandas get it in one line.

### C15 custom models (feature view in C15)
- `models/custom/<id>/model.json` (id, name, version, reads, schedule) + `model.py` with `fit(ctx)` and `assign(ctx, rows)`; `ctx` = the C14 handle + a scratch folder.
- Same interpreter, same Job Object limits, reads only `episodes.db` / the extract.
- **Live only through the built-in format:** a custom model that writes a conforming `model.json` (buckets, LSA, PCA, exemplars, radii) gets live C# assignment; otherwise it is batch-only (labels per closed activity on its schedule).
- Off until enabled, with a disclosure screen (network cannot be blocked without admin). Output: an extra card, or "workflow source" for C9.

### One learning framework: how D-B9 and C's models relate (D-C5)
| Element of the pattern | B (job kinds) | C (workflows) |
|---|---|---|
| Subject | a job kind (`KindId`) | the model; each cluster (`ClusterId`) |
| Learned answer | per-kind statistics (exact quantiles over 20 runs) | a fitted clustering + exemplars |
| Where it is learned | C# Core, streaming, at job end | Python worker, batch, at refit |
| Where it is applied | C# `LearnedThreshold` | C# `ExemplarAssigner` |
| Fallback (always safe) | the fixed number (10 min) | the **app** (`AppIdentity`); one level down the chain |
| Trust ramp | 5 → 20 good runs, linear blend | G0 data gate; per-cluster G3 (0.60 show, 0.75 "stable"); confidence floor + hysteresis |
| P1 in-distribution | run's cores outside the kind's range → fallback for this run | `d > r_c` → unassigned (app shown); > 50% unassigned over 3 days → early refit |
| P2 robustness | threshold jumped > 2× → keep the old one | assignment hysteresis; a refit with G5 < 0.80 is shown as "model changed" and its renames are re-checked |
| P3 bounds | clamp 2–60 min | confidence floor 0.5; names only from frequent terms; ≤ 40 clusters shown |
| P4 better than the fallback | learned false alarms ≤ fallback's (shadow count) | G6b (beats group-by-app); per cluster: "That's wrong" > 30% of the last 10 labels → hide that cluster until the next fit |
| P5 overhead | ≤ 100 KB, updates at job end | agent share ≤ 2 MB; refit under its Job Object; two over-time refits → **DEPRIORITIZE**: halve the fit cap and the bootstrap count |
| P6 liveness | restore a starved restrained job | model older than 28 days with refits failing or deferred → REPORT in B5 and the Model tab |
| REPLACE | `mode = fallback` for the kind | roll back to the previous model if it passed; else show apps |
| RELEARN | clear the kind's rings | request a refit (`relearn` action → `RefitPlanner`) |
| Labels | exit code, **It's fine**, End | rename, merge, hide, split, **That's wrong**, intruder answers |
| Explanation | `LearnedValue` on rows and toasts | `LearnedValue` on the card: "Tasktor dev · learned · 82% · stable" or "Code · default (unclassified)" |
| Off switch / forget | "Learn from my jobs", Forget kind / all | tracking off; forget range / all; Relearn (refit from scratch, ids reset) |
**Shared classes ( project-level changes to B's signed classes, listed for B in Consolidated at signing):** `LearnedValue` / `ValueSource` (unchanged); `GuardrailSet` generalised to `GuardrailSet<TSubject>` with per-section check providers (B registers kind checks, C registers model / cluster checks; shared: mode, the clean-runs / clean-fits hysteresis counter, the guardrail log); `KindSignature` generalised to `KeyedHasher` (one install key, domain prefixes `kind|` and `tok|`); the rules-file actions `use_fallback` / `relearn` and the guardrail log (B6.3 / H6). The flyout's "What Tasktor learned" becomes one view with two tabs, **Jobs** (B6) and **Workflows** (C6 / C8).

### OO model
| Object | Responsibility | Used by C | Reused by |
|---|---|---|---|
| `Scheduler` + `DemandLease` (shared) | 60 s tick; 5 s idle lease only while the user is idle | C1.2 | A, B, E, I |
| `IdleSource`, `SessionSource` (Windows, new) | `GetLastInputInfo` (backdated idle start), media-session check at idle start; WTS lock / unlock on the hook thread's message-only window | C1.2 | I3 (in a call), I4, I13 |
| `PowerEvents` (shared, A) | sleep / resume | C1.2 | A8, B |
| `ProcessTableSource` names-only + `ProcessExitEvents` (shared, B) | process start / stop | C1.3 | A2, B |
| `UiaDomainSource` (Windows, new) | UIA thread; `Request(window)` → `DomainRead` event; element cache per window; timeouts | C2 | F (later, if it needs UIA) |
| `NotificationSource` (Windows, new) | `UserNotificationListener`, app + time only | C1.5 | E (later) |
| `AppIdentity` (shared, A) | window → app identity key; WebView2 / Electron helpers → host;  `ApplicationFrameHost` → the hosted app | C1, C5 | A2, B, F, I |
| `PrivacyGate` (Core, new) | pause → app rule → private-window detector → title regex → redaction → domain rule, in that order, before any write | C3 | D (the snapshot never carries more than the gate allows) |
| `EpisodeBuilder` (Core, new) | event stream → segments, activities, windows, sessions; deterministic | C4 | I6, I13 |
| `KeyedHasher` (Core, from B's `KindSignature`) | HMAC-SHA256 with the install key, domain-separated | C4, C5 | B (kinds) |
| `FeatureExtractor` (Core, new) | tokenizer + hashing + blocks → sparse + dense vector | C5, C14 | C15 |
| `FrequentTerms` (Core, new) | candidate counting, promotion, expiry, hide-term | C7 | — |
| `EventStore`, `EpisodeStore` (Core, new) + `StoreWriter` (Agent host thread) | SQLite files, schema migrations, batched writes, retention, forget, extract writing | C1, C3, C4, C14 | H (settings export excludes them) |
| `WorkflowModel` + `ModelLoader` (Core, new) | mapped model, validation, swap, rollback | C1.6, C8.7 | C15 (live custom models) |
| `ExemplarAssigner` (Core, new) | features → cluster + confidence | C9 | C15 |
| `WorkflowTotals`, `SwitchCounter` (Core, new) | time per cluster today / week, frozen weeks; switches / interruptions | C9, C10 | I6 |
| `RefitPlanner` (Core, new) | refit triggers (rule), adaptive cadence, early refit, resume-from-stage | C8 | B5 (row), I3 / I4 / I13 (deferrals) |
| `GuardrailSet<T>` (shared, generalised from B) | P1–P6 checks, mode, hysteresis, log | C5–C9 | B |
| `RuleEngine`, `INotifier`, `IProcessControl` (shared) | guardrail and refit rules; the one refit-failure toast (B5); worker start / cancel | C8 | all |
| `WorkflowTracking` (Feature, new) | owns the parts above, the card, the snapshot block, the actions | C9 | G, D |

### New classes, and why each is a real gap
| New | Why the shared core does not already cover it |
|---|---|
| `WinEventSource` | no existing source pushes foreground / title events; A's slot map already reserves it for F, so C builds it once for both |
| `IdleSource`, `SessionSource` | no existing source reports input idle or lock; new implementations of `IEventSource`, not new interfaces |
| `UiaDomainSource` | UIA is a Windows seam and must run off the logic thread; modelled as a request → event source, like B's `ProcessExitEvents.Watch` |
| `NotificationSource` | new data (MSIX only) |
| `PrivacyGate` | the ordered gate is C-specific policy; it **reuses** `AppIdentity`'s ordered-rule matcher (rules resolved once per distinct app, as B's `JobGate` does) and compiled regexes. It is not a `RuleEngine` rule because it transforms data before storage, it does not trigger actions |
| `EpisodeBuilder` | sequence segmentation over events; nothing in the core builds closed time spans from events |
| `FeatureExtractor`, `FrequentTerms` | text features and the term life cycle are new data |
| `EventStore`, `EpisodeStore`, `StoreWriter` | the first section that stores a large, queryable history; B's byte-blob persistence does not fit range deletes and retention. Concrete classes, no interface (D-C9) |
| `WorkflowModel`, `ModelLoader`, `ExemplarAssigner` | applying an exported model online; nothing similar exists |
| `CurrentWorkflow`, `WorkflowTotals`, `SwitchCounter` | the card's state; small |
| `RefitPlanner` | when to run the worker; it is a policy with an adaptive interval, built from `RuleEngine` conditions + `LearnedValue`, not a new engine |
| `WorkflowTracking` | every section adds its own `Feature` |
| `IProcessControl.StartWorker` / `CancelWorker` | starting Tasktor's own child in a Job Object is an OS action; two members on an existing interface (B added five the same way) |
| Changes to B's classes: `KindSignature` → `KeyedHasher`; `GuardrailSet` → `GuardrailSet<TSubject>` | reuse instead of a parallel C hasher and a parallel C guardrail set |
No new interface (still seven). No new block kind. No new assembly.

### Data sources (one place)
| Data | Adapter | Windows source | Admin | Push or poll |
|---|---|---|---|---|
| Foreground window | `WinEventSource` | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT \| WINEVENT_SKIPOWNPROCESS)`; out-of-context events arrive in order on the hooking thread, which must pump messages **[verified: SetWinEventHook doc, 2026-11-03]** | no | **push** |
| Title change | same | `EVENT_OBJECT_NAMECHANGE` with `idProcess` = foreground PID; `GetWindowText` | no | **push** |
| Minimize / restore | same | `EVENT_SYSTEM_MINIMIZESTART` / `END` | no | **push** |
| Cloaked windows (virtual desktops, UWP) | same, at enumeration | `DwmGetWindowAttribute(DWMWA_CLOAKED)` (used in the probe) **[verified: local probe]** | no | on demand |
| Idle | `IdleSource` | `GetLastInputInfo` (session-wide last input time; 32-bit tick, wraps at 49.7 days) **[verified: local probe; wrap from the DWORD type]** | no | poll: 60 s active, 5 s idle |
| Media idle | `IdleSource` | WASAPI `IAudioSessionManager2` session enumeration for the foreground process, once at idle start **[unverified, A1]** | no | once per idle start |
| Lock / unlock | `SessionSource` | `WTSRegisterSessionNotification` on a message-only window on the hook thread **[unverified API detail, A1]** | no | **push** |
| Sleep / resume | A's `PowerEvents` | power broadcast | no | **push** |
| Process start / stop | B's `ProcessTableSource` (names-only) + `ProcessExitEvents` | Toolhelp diff on the 60 s tick; exit waits for watched jobs | no | poll 60 s (shared) / push |
| App identity | A's `AppIdentity` | exe basename, version info, package family / AUMID (`GetApplicationUserModelId` **[unverified right, A1]**) | no | on first sight of a process |
| Browser hostname | `UiaDomainSource` | UIA `ValuePattern` of the address bar; Chromium native UIA from 138 **[verified: developer.chrome.com, 2026-11-03]**, Firefox from 143 **[verified: release notes via search, 2026-11-03]** | no | on title change of a browser window |
| Notifications | `NotificationSource` | `UserNotificationListener` (package capability + user consent) **[verified: notification-listener doc, 2026-11-03]** | no | **push** |
| Own data | `EventStore` / `EpisodeStore` | SQLite via `winsqlite3.dll` | no | batched writes |
| Refit progress | B's `WorkerProgressSource` | the worker's stdout | no | **push** |

### Time and space per operation (e = events/day ≈ 5–10k, a = activities/day ≈ 300, t = tokens per title ≈ 3–10, V = kept buckets ≤ 10k, C = clusters ≤ 40, E = exemplars ≤ 800, n = activities in a fit ≤ 10k)
| Operation | Naive | Design | Exact? |
|---|---|---|---|
| Title events | global hook, filter in the callback: O(all UI name changes) | hook scoped to the foreground PID + 1 s coalescing: O(changes of the foreground window) | yes for the foreground window (the only one C records) |
| Idle detection | poll every 5 s | 60 s poll with backdated start; 5 s only while idle | idle start exact; idle end ≤ 5 s late |
| Privacy gate | match every rule on every event, O(r) regex per event | app rules resolved once per distinct app (cached); title regexes compiled once; O(r_title · len) per **changed** title | yes |
| Redaction | — | one pass of compiled patterns, O(len) | yes |
| Tokenize + hash | hash at every event | tokens counted in memory per open activity (≤ 256, `TopN`); hashed once at activity close, O(t) HMACs per distinct token | yes |
| Episode building | re-scan raw events | state machine, O(1) per event | yes |
| Raw writes | one transaction per event | batched (10 s / 64 events), O(1) amortised | yes (≤ 10 s at risk on a crash) |
| Assignment | full vector over 2^18 | binary search per token in `buckets.i32`, O(nnz log V); LSA O(nnz · 64); PCA O(100 · 16); exemplars O(E · 16) | yes (floating point, parity ≤ 1e-5) |
| Card totals | SQL per refresh | in-memory per cluster, O(C) per card | yes |
| Retention purge | scan the table | delete by indexed `ts_utc_ms`, O(deleted) | yes |
| Frequent terms | count all words forever | candidates with a 30-day bitmap, promoted at 5 / 3, O(t) per activity close | yes |
| Training extract | export rows as JSON | binary CSR + f32, O(n · nnz) | yes |
| Refit (worker) | LSA on 2^18 columns, HDBSCAN on ~100 dims | df ≥ 3 columns (0.3 s at 5k), 16 dims (0.9 s HDBSCAN at 5k, 4.9 s at 10k), 20 bootstraps ≈ 20 s at 5k [verified: local probe, synthetic; bootstrap extrapolated from single fits] | yes for the chosen method |
| Id matching | compare all pairs by centroid in different bases | Jaccard on member sets: O(n) to build the C_old × C_new overlap table, Hungarian O(C³) | yes |
| Motifs | PrefixSpan over all sequences | n-gram counts per session, O(len · 3) | yes for contiguous patterns |
| Space (agent) | — | open episode + windows table + model table + totals ≈ 100–200 KB private; model sidecars mapped | yes |

### Type design: structures, classes, interfaces (D-C13; design, not code)
**1. Where every C type lives (A's four assemblies)**
| Assembly | C types |
|---|---|
| `Tasktor.Contracts` | `WindowKey`, `WindowEvent` + `WindowEventKind`, `PresenceEvent` + `PresenceKind`, `DomainRead`, `NotificationArrived`, `CaptureEvent` (stored row), `Segment`, `Activity`, `WorkflowWindow`, `Session`, `ClusterId`, `Assignment`, `ModelManifest` (model.json schema), `TrainingExtract` schema, `WorkerJob` (stdin), `EvalReport` (eval.json), `Overrides`, the snapshot `state.workflow` block |
| `Tasktor.Core` | `PrivacyGate`, `Redactor`, `EpisodeBuilder`, `Tokenizer`, `KeyedHasher` (shared), `FeatureExtractor`, `FrequentTerms`, `EventStore`, `EpisodeStore`, `WorkflowModel`, `ModelLoader`, `ExemplarAssigner`, `CurrentWorkflow`, `WorkflowTotals`, `SwitchCounter`, `ClusterFeedback`, `RefitPlanner`, `GuardrailSet<T>` (shared), `WorkflowTracking`, `WorkflowCardBuilder` |
| `Tasktor.Windows` | `WinEventSource`, `IdleSource`, `SessionSource`, `UiaDomainSource`, `NotificationSource`, the `StartWorker` / `CancelWorker` half of `Win32ProcessControl`, `DpapiKeyStore` (the install key and the C3.5 data key) |
| `Tasktor.Agent` | wiring; the `StoreWriter` thread; key unwrap at start |
| Python (repo `python/`) | `tasktor_worker` (stages), `tasktor` (C14 API), `tasktor_reference` (episodes, features, assigner: parity only) |
| Tests | `Tasktor.Core.Tests`: fake sources, recorded event streams (synthetic titles only), the fake clock, a fixture model; Python: parity tests against the same fixtures |

**2. Key records (Contracts)**
| Type | Fields | Notes |
|---|---|---|
| `WindowKey` (readonly struct) | `Id` (int, per-install counter) | maps (hwnd, `ProcKey`) → id in `KeyedTable` |
| `WindowEvent` | `Kind` (Foreground, TitleChanged, MinimizeStart, MinimizeEnd), `Hwnd`, `Proc` (ProcKey), `AtMono`, `AtUtc` | hook thread fills only these; title text is read on the logic thread side by the source's worker step, not in the callback |
| `PresenceEvent` | `Kind` (IdleStart, IdleEnd, Lock, Unlock, Suspend, Resume), `AtUtc` (backdated for IdleStart), `IdleKind` | |
| `DomainRead` | `Window`, `Host` (string?), `Outcome` (Ok, Focused, NotHost, Timeout, Unsupported) | outcomes count toward the C2.4 guardrail |
| `Activity` | `Id`, `App`, `Start`, `End`, `ActiveMs`, `IdleMs`, `Glances`, `TitleChanges`, `PrevApp`, `NextApp`, `JobFrac`, `Day`, `Tokens` (bucket → weight, ≤ 256) | the unit of clustering |
| `Assignment` | `ClusterId?`, `Confidence`, `Level` (Workflow, Activity, App), `Basis` (`LearnedValue`) | what the card shows |
| `ModelManifest` | see the model files table | validated before swap |

**3. Core classes (state, operations, invariants)**
| Class | Owns | Operations | Invariants | Cost |
|---|---|---|---|---|
| `PrivacyGate` | compiled rules, private-window detectors, pause flag | `Apply(rawEvent) → Drop / Exclude(span) / Keep(redacted)` | runs before **every** write; pause wins over everything; a browser without a passing detector never yields a title or domain | O(rules on changed titles) |
| `EpisodeBuilder` | open segment, open activity (tokens ≤ 256), open window, open session | `On(CaptureEvent) → closed records` | deterministic: same events → same records (parity); gaps never become idle | O(1) per event |
| `FeatureExtractor` | tokenizer config, `KeyedHasher` | `Of(activity) → (sparse, dense)` | identical to the Python reference within 1e-5 | O(tokens) |
| `FrequentTerms` | candidates (raw side), promoted terms, hidden terms | `Observe(activity)`; `Promote()` nightly; `Hide(term)` | a term is readable after 30 days only if promoted and not hidden | O(tokens) per activity |
| `EventStore` / `EpisodeStore` | one connection each (write side on `StoreWriter`) | `Append(batch)`; `Purge(olderThan)`; `Forget(range)`; `WriteExtract(range) → path + hash` | `forget` removes raw and derived rows in one transaction per file | O(batch) |
| `WorkflowModel` | mapped sidecars + parsed manifest | `Open(dir)`; `Close()` | read-only; never mutated after open | O(1) |
| `ModelLoader` | current + 2 previous versions | `Validate(staging) → ok / reason`; `Swap()`; `Rollback()` | a model whose `eval.json` failed activation is never swapped in | O(file sizes) once |
| `ExemplarAssigner` | reference to the current `WorkflowModel` | `Assign(features) → (cluster?, confidence)` | `d > r_c` → none | microseconds |
| `CurrentWorkflow` | last 2 assignments per level, current shown value | `Update(assignment)`; `Shown() → Assignment` | changes only after 2 agreeing results; falls back level by level | O(1) |
| `WorkflowTotals` | per cluster: today, this week; frozen past weeks | `Add(activity, cluster)`; `Relabel(model)` (after a swap, from `episodes.db`) | totals add up to active time (Unclassified and Private are rows) | O(C) |
| `ClusterFeedback` | per cluster: last 10 labels (bitmask), shown count | `Wrong(cluster)`; `Shown(cluster)` | > 30% wrong → `GuardrailFired(hide cluster)` | O(1) |
| `RefitPlanner` | last fit time, interval (`LearnedValue`), deferrals, resume stage | `Due(now, state) → bool`; `OnFitResult(eval)`; `RequestEarly(reason)` | at most one worker at a time; never on battery; resumes from the last checkpoint | O(1) |
| `WorkflowTracking : Feature, ICardProvider` | the parts above | `Enable()` / `Disable()` (install / remove hook, open / close stores, map / unmap model); `BuildCard(size)`; `Act(action)` | Disable releases everything (D-C7 acceptance 1) | O(1) |

**4. Interface members C adds (no new interface)**
| Interface | Member | In / out | Failure behaviour |
|---|---|---|---|
| `IProcessControl` | `StartWorker(WorkerSpec) → ProcKey` | exe, args, stdin bytes, Job limits, priority, EcoQoS | creation fails → result event with the reason; no retry loop |
| `IProcessControl` | `CancelWorker(ProcKey)` | the worker's key | terminates the Job; idempotent |
| `IEventSource<WindowEvent>` | `WinEventSource` (+ `FocusProcess(pid)` to move the scoped title hook) | — | hook install fails → C1 reports "capture unavailable", tracking stays off |
| `IEventSource<DomainRead>` | `UiaDomainSource.Request(WindowKey, hwnd)` | — | timeout → `DomainRead(Timeout)`, never blocks the loop |

**5. Threading (D-B11 rule, unchanged)**
- **Hook thread:** `SetWinEventHook` + message loop + the WTS message-only window. Callbacks copy five numbers into an event and enqueue; the title text is read by `GetWindowText` on the source's own step before enqueueing (never on the logic thread, so a slow call cannot stall sampling).
- **UIA thread:** domain reads with a timeout; replies are events.
- **Logic thread:** gate, episode builder, features, assigner, totals, rules: all C state, no locks.
- **Store writer thread:** owns the write connections; receives batches; writes the extract; posts completion events.
- **Worker:** a separate process; its stdout and exit come back as events (B's sources).

**6. Main flows**
1. **Title change:** hook callback (foreground PID) → enqueue → `GetWindowText` → `PrivacyGate.Apply` → `EpisodeBuilder.On` → (debounced 5 s) `FeatureExtractor` on the open activity → `ExemplarAssigner` → `CurrentWorkflow.Update` → snapshot if the shown value changed; the event batch → `StoreWriter`.
2. **Idle:** 60 s tick → `IdleSource` sees last input ≥ 3 min ago → `PresenceEvent(IdleStart, at = lastInput)` → builder closes the segment at that time → 5 s lease starts → input returns → `IdleEnd` → lease released.
3. **Refit:** rule fires → `RefitPlanner.Due` → `StoreWriter` writes the extract → `StartWorker` → stdout stages → B5 row → exit 0 → `ModelLoader.Validate` → `Swap` → `WorkflowTotals.Relabel` → card.
4. **That's wrong:** card action → `ClusterFeedback.Wrong` → guardrail check → maybe hide the cluster → `CurrentWorkflow` falls back a level for that cluster until the next fit.

**7. Memory layout (fits D-C7's ≤ 2 MB)**
| Item | Size | Count | Total |
|---|---|---|---|
| Windows table (`WindowRow`: key, hwnd, ProcKey, app, last title id) | ~64 B | ≤ 500 per run | ~32 KB |
| Open activity tokens (string id + weight) | ~16 B + interned text | ≤ 256 | ~16 KB |
| Event batch awaiting write | ~150 B | ≤ 64 | ~10 KB |
| Model manifest + cluster table + feedback | ~200 B per cluster | ≤ 40 | ~8 KB |
| Totals (today, week, 14-day switches) | small | — | ~2 KB |
| SQLite page caches (2 connections × 256 KB) | — | — | 512 KB |
| Mapped sidecars | file-backed | — | 0 private (A1 to confirm) |
| **Total** | | | **≈ 0.6 MB** (budget 2 MB leaves headroom for SQLite's own state) |

**8. Test seams**
| Seam | Fake | Lets a test |
|---|---|---|
| `IEventSource<WindowEvent>` / `<PresenceEvent>` / `<DomainRead>` | scripted streams (synthetic titles) | build every episode fixture: glance, lock mid-run, clock jump, DST, pause, excluded app in the middle |
| `IProcessControl` (worker members) | fake worker that prints stages, fails, hangs | B5 rows, watchdog, resume from stage |
| `IClock` | fake clock | 90-day windows, weekly freezes, adaptive cadence in milliseconds |
| SQLite | in-memory / temp-file databases (real engine) | retention, forget, extract round-trips on Linux CI |
| Model | a fixture model from the Python reference | C# vs Python assignment parity |

### Reuse summary for sibling sections
- **`IdleSource` / `SessionSource`** give I3 (meeting guard), I4 and I13 presence without new hooks; I11 (required-window watch) uses `WinEventSource` title events.
- **The training extract and C14 API** are the research harness: any model (C15, or the user's own notebooks) runs on exactly the data the built-in model saw.
- **`KeyedHasher` and `GuardrailSet<T>`** are now shared between B and C; any later learned policy (A6 memory growth, A5 disk-fill rate, I13) uses the same pair.

## C1. Passive event capture
**Status:** accepted (signed 2026-11-04). Module (asked on first open of the workflow card). Phase A1.

**What the user sees**
| Surface | Content |
|---|---|
| S / M / L | nothing of its own; the C9 card shows "Tracking paused" or "Tracking off · Turn on" when relevant |
| Flyout → Workflows → Privacy | on / off, Pause, what is recorded today (counts by kind), excludes, retention, Forget |
| Opt-in screen (C1.7) | three short lists: **Recorded** (app, window title after redaction, browser hostname, idle / lock times, process start / stop), **Never recorded** (the binding list above), **Excluded by default** (C3.2); one **Turn on** button; a link to the full privacy page |
| Tray (G6) | Pause tracking / Resume tracking |

**Data sources:** Architecture → Data sources (foreground, title, minimize, idle, lock, sleep, process start / stop, notifications).
**When it runs:** only while tracking is on and not paused. Hooks are events (push); idle polls 60 s while active, 5 s while idle; process start / stop rides B's 60 s scan.
**Defaults and rules:** off until turned on; title debounce 1 s; flicker < 1 s ignored by the builder; notifications off inside C1 (C1.5); system UI ignored.
**Actions:** turn on / off, pause / resume (persisted), forget (C3.4).
**Privacy:** every event passes `PrivacyGate` before the batch reaches the store writer; nothing in this section reads text other than the window title and the address-bar hostname.

**Edge cases**
- UAC secure desktop, lock screen: no events reach the hook; recorded as the gap they are (lock / unlock, or a foreground jump with no dwell credited to system UI).
- Elevated windows: foreground events still arrive out of context; their titles may be unreadable from medium integrity **[unverified, A1]** → app-only rows.
- `ApplicationFrameHost.exe` (UWP / packaged apps): the foreground window belongs to the frame host; `AppIdentity` must resolve the hosted app ( change for A).
- Several monitors: one foreground window at a time; only it counts.
- Virtual desktops: cloaked windows are not foreground; switching desktops is a foreground change.
- Full-screen games and video: foreground app with no title changes; media-idle keeps "watching" from counting as away.
- `GetLastInputInfo` tick wrap at 49.7 days of uptime: computed with unsigned 32-bit difference.
- Clock jumps and DST: ordering by `mono_ms`, display by `ts_utc_ms` + `tz_min`.
- Remote Desktop sessions: the agent runs per session; data stays per user profile.
- Agent crash: at most 10 s of events lost; the next start writes `agent_start` and the builder treats the gap as missing, not idle.

**Acceptance**
1. Over a 2-hour scripted session on the reference PC: every foreground change ≥ 1 s appears with the right app (manual + script).
2. Zero rows while paused or in excluded apps: byte search of both DB files for fixture titles (test).
3. Idle start within ±1 s of the scripted last input, idle end within 5 s (fake clock test; manual check on the PC).
4. A background window renaming 1,000 UI objects a second adds no measurable agent CPU (D-C7 test 3).
5. No `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hook is ever installed (process inspection test).
6. Turning tracking off removes the hook and closes both stores within 1 s (handle / hook count test).

## C2. Browser domain
**Status:** accepted (signed 2026-11-04) (kept from 2026-11-03, revised for the probe). Inside C1. Phase A1. Catalog status stays **limited** (per-browser).

**What the user sees:** nothing directly. Domains appear as `dom:` evidence in cluster detail (flyout: "top sites: github.com, docs.python.org") and help separate browser activities ("Chrome · course reading" vs "Chrome · email").
**Data source:** UIA address bar via `UiaDomainSource`; Chromium native UIA from 138, Firefox from 143 (Architecture). Address-bar locators per browser come from fixtures written with agent-desktop's tree snapshots at development time (D-C14).
**When it runs:** on a foreground or title change of a browser window only; element found once per window and cached (cold find 661 ms, warm 24 ms on the probe); reads on the UIA thread with a 1 s timeout.
**Defaults and rules**
- A read is discarded if the address bar has keyboard focus (the field then holds what the user is typing) or the text does not parse as a hostname with a public suffix.
- Stored as `domain` and as a `dom:<host>` token; no path, query or fragment ever leaves the source.
- **Private windows (C2.3):** each browser needs a detector (a title marker or the profile button's UIA name such as "Incognito" / "InPrivate") that passes an A1 probe. A browser whose detector has not passed is **app-only** (no title, no domain).
- ** Cost guardrail (C2.4, P5):** more than 3 timeouts in 10 reads, or a measured rise of the browser group's CPU (A2 data, with reads on vs off, sampled at A1 and then once a week while a viewer is open) above 1 point, switches domain reads off for that browser and logs why; the Privacy tab shows "domain reads off for Chrome: too slow".
**Actions:** per-browser on / off in the Privacy tab.
**Privacy:** the hostname is the only UIA text Tasktor ever reads; private windows are excluded as a whole.
**Edge cases:** browser updates that rename the address bar (detector / locator fails → that browser drops to app-only and reports it); PWAs and app windows (`--app=`) have no address bar (title only); several profiles; split view or side panels; a browser hung by a page (UIA timeout, no retry storm).

**Acceptance**
1. Per supported browser, 50 scripted tab switches → ≥ 95% correct hostnames, 0 reads while typing in the address bar, 0 records from a private window (manual, A1).
2. Browser CPU rises ≤ 1 point with reads on vs off over 10 min of scripted browsing (manual, A1).
3. A fake UIA source timing out 4 times in 10 reads turns reads off for that browser and logs the reason (test).
4. A cold read never blocks the logic thread: a 2 s fake UIA delay leaves event handling latency unchanged (test).

## C3. Privacy gate: pause, excludes, retention, encryption
**Status:** accepted (signed 2026-11-04) (kept, plus C3.6). Core of C1. Phase A1.

**Order of the gate (before anything is written):** pause → app rule → private-window detector → title regex rule → redaction → domain rule.
**Three rule strengths:** `drop-title` (keep app and time, drop title and domain); `exclude` (write only `excluded_start` / `excluded_end`; the time shows as one **Private** row, so totals still add up); `ignore` (as if the window never had focus).
**Default list (editable; shown in plain words at opt-in):** password managers (1Password, Bitwarden, KeePass / KeePassXC, LastPass, Dashlane, Proton Pass), Windows credential prompts, Windows Security → `exclude`; private browser windows → `exclude`; messengers (WhatsApp, Signal, Telegram, Discord, Teams chat, Slack) and mail (Outlook, Thunderbird) → `drop-title`.
**Redaction (always on):** email addresses → `‹email›`; runs of 4+ digits → `‹num›`; UUIDs and hex ids → `‹id›`; the Windows user name and display name → `‹me›`; drive-path shapes → `‹path›`. Redaction tokens never enter features.
**Encryption (optional, C3.5):** field-level AES-GCM on `title`, `domain` and `term_candidate` text in `events.db`, and on promoted terms in `episodes.db`; the data key is wrapped by DPAPI (user scope) and unwrapped by the host at start. SQLite stays plain (no SQLCipher). The worker never needs the key: the extract has no text.

**What the user sees**
| Surface | Content |
|---|---|
| S / M / L | "Paused" state on the C9 card; Private time as its own row |
| Flyout → Privacy | pause, rules list (app / title pattern → strength), retention, encryption toggle, ** What Tasktor knows** (C3.6): raw events (count, oldest), activities, promoted terms (count; list with **Hide**), models (versions, sizes), Forget range / all |
| CLI | `tasktor pause`, `resume`, `forget`, `excludes list|add|remove` |

**Edge cases:** a rule added later does not rewrite history automatically; the UI offers "also forget past matches" (a range forget by app or pattern); renamed apps (identity key unchanged → rule still applies); the user's own name inside a title in another form (nickname) is not redacted, which is why messengers default to `drop-title` and Hide a word exists.

**Acceptance**
1. Fixture titles that hit each rule strength never appear in either DB file (byte search, test).
2. Pause survives a reboot; while paused only `pause` / `unpause` rows are written (test + manual).
3. After the nightly purge no raw row is older than 30 days + 1 (test, fake clock).
4. `forget --since --until` leaves no segment, activity, token row, assignment or total in the range (test).
5. With encryption on, a byte search of `events.db` finds no fixture title, and the worker's extract is byte-identical to the unencrypted run (test).

## C4. Episodes
**Status:** accepted (signed 2026-11-04) (definition in Architecture → Episode definition). Built online in C#; Python reference. Phase L1 (records start at A1).
**What the user sees:** none directly; the flyout's cluster detail lists recent activities ("Code · 42 min · 10:05–10:47").
**Data source:** the gated event stream. **When:** on every event (O(1)). **Defaults:** thresholds 2 s / 30 s / 3 min / 5 min / 60 min / 15 min / 3 min in `prereg.json`.
**Edge cases:** an excluded app in the middle of an activity (the activity splits; Private time between); a pause mid-activity (closes it); clock jumps (`mono_ms`); a 3-hour single-app marathon (three chunks); a glance into an excluded app (counted as a glance with no title).

**Acceptance**
1. ≥ 20 hand-built fixture streams (Alt-Tab glance, lock mid-run, clock jump, DST, pause, excluded app in the middle, sleep, agent crash gap) give identical segments, activities, windows and sessions in C# and the Python reference (test, both CIs).
2. Active time summed over activities equals foreground time minus idle and excluded time (test).
3. The builder never emits an activity spanning an `agent_stop` gap (test).

## C5. Features and clustering
**Status:** accepted (signed 2026-11-04) (Architecture → Features, Clustering). Bundled Python worker. Phase L1.
**What the user sees:** clusters through C9 / C7; the Model tab lists clusters with apps, top terms, top sites, typical hours, stability, size.
**Data source:** the training extract (numbers only). **When:** only inside a refit (C8).
**Defaults:** blocks and weights as tabled; df ≥ 3; LSA 64; PCA 16; HDBSCAN `max(8, 1% n)` / 5 / eom; k-means k 5–40 by silhouette; fit cap 10k; motifs support 5 sessions / 3 days, lift 2.
**Edge cases:** one dominant app (most time in a browser: the text and `dom:` blocks carry the split; G6 shows whether it worked); very short titles (median 3 tokens on the probe): app and context blocks matter more, which the weights already reflect; a new app after the fit (`other` one-hot; P1 sends it to the app fallback); non-Latin scripts (the tokenizer splits on Unicode letter / digit classes **[unverified for CJK; L1 report]**).

**Acceptance**
1. The planted-topic synthetic set recovers its topics (ARI ≥ 0.9) for both k-means and HDBSCAN (test; probe gave 1.0).
2. A synthetic set where titles are pure noise and only the app is informative fails G6b, and C shows apps (test: the null model wins).
3. The C# `FeatureExtractor` and the Python reference produce identical sparse and dense vectors within 1e-5 on the fixtures (test).
4. A 10k-activity extract fits within D-C7's limits (manual, L1).

## C6. Label-free evaluation
**Status:** accepted (signed 2026-11-04) (gates in Architecture → Label-free evaluation). Phase L1.

**What the user sees**
| Surface | Content |
|---|---|
| S / M / L (before activation) | progress card: "142 / 300 activities · 4 / 7 days · 11 / 20 h" with a `Progress` block |
| B5 refit row | stage, %, then "passed 6 / 6 gates" or "failed G6b: clusters add nothing beyond apps · showing apps" |
| Flyout → Model | each gate: value, threshold, pass / fail, the null model's value beside it; per-cluster stability badges; G7 sensitivity as a small table; `prereg` version and hash |
| CLI | `tasktor clusters --eval` prints `eval.json` |

**C6.4 Human check (, optional, never a gate in the public app)**
- **Intruder test:** 5 activities from one cluster + 1 from another (text shown from frequent terms only); the user picks the odd one out; 10 rounds ≈ 3 min. ≥ 70% correct across 10 clusters = the clusters mean something to a person. Reported in `eval.json`, never blocks activation.
- **Self-label sample (the horizon card's test):** 20 random recent activities, the user types a short free label for each; the worker reports NMI(clusters, labels) and which labels the clusters merge or split. Labels stay local in `episodes.db` and are deletable. On the user's install this is the cheapest version of "can you label your own stream".
**Pre-registration rule:** thresholds live in `prereg.json` v1; any change bumps the version with a written reason before the next fit (D-C6).
**Edge cases:** the gates pass but the clusters are dull (the human check exists for this); G4 fails because work changed between odd and even days (a real signal: stays unshown); too few clusters for G6 (≥ 3) on a narrow user (shows apps).

**Acceptance**
1. Every fit writes `eval.json` with every gate, value, threshold, the null model's values and the `prereg` hash (test).
2. A model failing any activation gate is never swapped in; the card keeps the previous model or apps (test).
3. A cluster with G3 < 0.60 is never shown by name (test).
4. Changing a threshold without bumping the `prereg` version fails CI (test).

## C7. Naming, rename, merge
**Status:** accepted (signed 2026-11-04) (Architecture → Naming and merging). Phase L1.

**What the user sees**
| Surface | Content |
|---|---|
| M / L | the workflow name; ** That's wrong** in "…" |
| Flyout → Workflows | per cluster: Rename, Merge into…, Hide, Split, top terms with **Hide a word**, "named by you" vs "auto" badge |
| CLI | `tasktor clusters`, `tasktor name <id> "…"`, `tasktor merge <a> <b>`, `tasktor hide <id>`, `tasktor split <id>`, `tasktor hide-term <term>` |
** That's wrong (C7.3):** marks the current assignment as wrong. It does not ask what is right (no labelling chore). Effects: the card drops one level down the chain for this activity; `ClusterFeedback` records it; > 30% wrong over the cluster's last 10 labels hides the cluster until the next fit (P4) and logs it; the next refit sees the labels as cannot-link hints only in `eval.json` (reported, not used in the fit, so the method stays unsupervised).
**Edge cases:** renaming a cluster that later splits (the larger child keeps the name; the other gets an auto name with "split from …"); merging clusters from different apps (allowed; a workflow-level concept); a hidden term that was a cluster's only distinctive word (the name falls back to `<app> · <next terms>`).

**Acceptance**
1. Auto names are identical for the same model (test).
2. A rename survives 3 refits whose matching keeps the id (test with fixture fits).
3. Names never contain a term outside the frequent dictionary or a hidden term (test).
4. Four "That's wrong" in the last 10 labels hide the cluster and log a guardrail event; three do not (test).

## C8. Refit and stable ids
**Status:** accepted (signed 2026-11-04) (Architecture → Refit cadence and stable ids; Python worker). Phase L2 (by hand at L1).

**What the user sees:** B5's refit row ("Refit · stage bootstrap · 60% · limited"); in the Model tab: last fit, next fit condition ("when idle and on AC; interval 14 days, learned"), the model history (version, date, clusters matched / new / retired), Rollback.
**Data source:** the extract; previous model; overrides. **When:** triggers as specified; never on battery (I4), never in a meeting or event mode (I3, I13).
**Defaults and rules (rules file):** once a day at most; idle ≥ 10 min; AC; ≥ 50 new activities or ≥ 7 days; adaptive interval 7 → 28 days; early refit when > 50% unassigned over 3 active days; fit window 90 days, cap 10k; Job Object 25% CPU / 1.5 GB; 2 BLAS threads; watchdog 20 min.
**Actions:** Refit now, Cancel, Pause (cancel + resume from the last stage, B5), Rollback to the previous model, Relearn (refit from scratch; ids reset; asks first because names and history are re-attached by matching only).
**Edge cases:** the user returns mid-refit (the refit continues at its limits; it is already capped); laptop unplugged mid-refit (I4 pauses it: cancel + resume); disk full (extract write fails → refit skipped, REPORT); a model that matches poorly (G5 < 0.80: activates if its gates pass, shown as "model changed: 3 new, 2 retired" so renames can be checked); a split request on a cluster that no longer exists.

**Acceptance**
1. Renamed clusters keep their ids across refits on fixture data with 20% new activities (test).
2. A refit never starts on battery or in a meeting (test with fake power / I3 state).
3. Cancel during `bootstrap` and re-run: the worker resumes at `bootstrap` and the result equals an uninterrupted run (test with the fake worker; real worker at L1).
4. Three refits with G5 ≥ 0.95 and no new cluster double the interval twice (7 → 14 → 28) and the next one stays at 28; a G5 drop resets to 7 (test, fake clock).
5. A closed week's totals are unchanged after a refit that relabels its activities (test).
6. Rollback restores the previous model and its assignments (test).

## C9. Current workflow + time per workflow (X1)
**Status:** accepted (signed 2026-11-04). Phase L2 (the card shows the app fallback and the progress card from A1).

**What the user sees**
| Surface | Content |
|---|---|
| S | line 1: workflow (or activity cluster, or app) with a confidence dot; line 2: time in it today. E.g. "● Tasktor dev · 1 h 20 m"; fallback "Code · unclassified · 40 m" |
| M | current (name, level badge, activity under it: "in Code · tasktor"), time today; top 3 workflows today as bars; Unclassified and Private rows when ≥ 10% of the day |
| L | M + this week per top workflow (rows with a bar each), switches today vs the 14-day median (C10), actions: **That's wrong**, **Pause tracking** |
| Flyout → Workflows | Today / Week / 30 days; table of workflows (time, share, stability badge, auto / named); drill-down to activity clusters, apps, top terms, top sites, typical hours, motifs ("usually Code → Terminal → Chrome docs"); Rename / Merge / Hide / Split |
| Before activation | progress card (C6); the S card shows the app fallback with "learning · 4 / 7 days" |
**Blocks used:** `Metric`, `Badge`, `Bar`, `Table`, `Sparkline`, `Progress`, `Actions` (no new block; a stacked day timeline would be a G decision later).
**Fallback chain (D-C1):** workflow (if the workflow sub-model is active and confident) → activity cluster → app. The level and source are always visible ("learned · 82% · stable" / "default: app").
**Data source:** `CurrentWorkflow`, `WorkflowTotals`. **When:** card values change only on assignment changes and once a minute (time today).
**Edge cases:** midnight (today's totals roll at local midnight using `tz_min`); travel across time zones (days by local time at capture); long idle (the card says "away since 14:02"); paused tracking (the card says "Tracking paused" and keeps today's totals so far); Private-only stretches.

**Acceptance**
1. Shown workflow changes only after 2 agreeing assignments (test).
2. Below the floor the card shows the next level down with "default" (test).
3. Today's workflow totals + Unclassified + Private = active time today (test).
4. Card values come from in-memory totals: no SQL query per viewer refresh (counter test).
5. On the user's PC after L2, the S card's workflow matches what the user says they are doing in ≥ 7 of 10 spot checks over a week (manual; recorded, not a gate).

## C10. Switches and interruptions
**Status:** accepted (signed 2026-11-04). Phase L2.
- **Switch:** a change of activity cluster where both sides last ≥ 2 min. **Interruption:** A → B → A with B < 5 min. Counted per day by `SwitchCounter`; 14-day sparkline (`RingBuffer` of 14).
- **What the user sees:** L card line "Switches 23 today (usual 18)"; flyout chart with interruptions and their most common B (often messengers or mail; app-level only for `drop-title` apps).
- ** Learned "usual":** the median of the last 14 active days (an exact window, like B's rings); shown only with ≥ 7 days.
- **Edge cases:** unclassified on both sides (counts as a switch only if the apps differ); notifications (if C1.5 is on) shown as the likely trigger, never claimed as the cause.
**Acceptance:** 1. Fixture sequences give the expected switch and interruption counts (test). 2. "Usual" is hidden with fewer than 7 days (test).

## C11. Unusual episode (later)
**Status:** later. An activity with no assignment for ≥ 20 min, or a workflow window far from every workflow exemplar (> 2 × radius), is flagged privately in the flyout ("an unusual 45 min on Tuesday"). Never an alert, never a toast. Ships only after C9 has run for ≥ 30 days and the flag rate is < 1 per active day on the user's install (pre-registered).

## C12. Next activity / workflow prediction (later)
**Status:** later. Baselines: "same as now" and a first-order Markov chain over clusters. Ships only if top-1 accuracy beats the Markov chain by ≥ 5 points on held-out days (pre-registered), and only as a suggestion surface for I5 (presets). Needs ≥ 30 days.

## C13. Self-use counts → move to H
**Status:** accepted: moves to H (H14, Core, A1). Card opens, CLI calls, rename / merge actions, counted locally in a small numbers-only file (not in `events.db`, because tracking may be off). Feeds the Lifecycle review. Nothing in C depends on it.

## C14. Export and Python API
**Status:** accepted (signed 2026-11-04) (Architecture → C14). Module (inside C1). Phase L1.
**What the user sees:** flyout → Privacy → **Export my data** (folder picker, range, CSV / JSONL); the CLI; `docs/data.md`.
**Privacy:** default exports carry frequent terms only. The live API is read-only and never opens `events.db`.
**Edge cases:** export while a refit runs (read-only, WAL readers are fine **[unverified detail for `mode=ro` + WAL, L1]**); schema upgrades (the API checks `schema_version` and refuses newer schemas with a clear message).

## C15. Custom models (Module, later)
**Status:** later (Architecture → C15). Contract kept from 2026-11-03.
**Acceptance (when built):** 1. A sample custom model in the repo fits, exports the built-in format and is assigned live by C# with parity (test). 2. A custom model exceeding its memory cap is ended and reported, and the built-in model is unaffected (test). 3. Enabling shows the network disclosure first (UI test).

## Data thresholds
| Step | Needs |
|---|---|
| L1 activity clusters (G0) | ≥ 300 activities **and** ≥ 7 distinct active days **and** ≥ 20 h active |
| L1 workflows | ≥ 200 workflow windows over ≥ 10 days |
| Motifs | ≥ 30 sessions |
| C10 "usual" | ≥ 7 active days |
| Embedding trial | only after a TF-IDF fit fails |
| C11 / C12 | ≥ 30 days of C9 |

1. `CaptureEvent`, `Activity`, `Assignment` (records): which fields are optional; what never appears.
2. `PrivacyGate`: the ordered steps, what each returns, the pause-wins invariant, rule caching per app.
3. `WinEventSource`: the hook thread, the scoped title hook (`FocusProcess`), what the callback may and may not do, unhooking on disable.
4. `IdleSource`: the 60 s / 5 s lease switch, the backdated idle start, the tick wrap.
5. `EpisodeBuilder`: the four-level state machine, its closing rules, gap handling; the parity fixtures it must pass.
6. `KeyedHasher` + `Tokenizer` + `FeatureExtractor`: what is hashed, domain prefixes, the ≤ 256-token bound, dense blocks.
7. `EventStore` / `EpisodeStore` + `StoreWriter`: schema, batching, purge, forget, extract writing; who owns which connection.
8. `ExemplarAssigner` + `WorkflowModel`: the mapped sidecars, the projection steps, confidence, the parity tolerance.
9. `CurrentWorkflow`: the fallback chain, floor, hysteresis, `LearnedValue`.
10. `RefitPlanner`: triggers, adaptive interval, early refit, resume stage; how it composes `RuleEngine` conditions.
11. `GuardrailSet<TSubject>` (with B): the shared core and the C check provider (P1–P6 as tabled).
12. Python `tasktor_worker`: the six stages, checkpoint layout, the `{stage, pct, msg}` lines, the output files.

## Open questions
Most are checks to run in the named phase; rows marked **decide** need the maintainer.
| # | Question | Resolved by |
|---|---|---|
| OQ-C1 | ~~Long-term text~~ → proposed D-C4 (keyed hashes + frequent-term dictionary, 5 activities / 3 days) | **decide** ( D-C4) |
| OQ-C2 | Workflows (cross-app windows) as the headline, with activity clusters and apps as fallbacks (D-C1), or activity clusters only in v1? | **decide** ( D-C1) |
| OQ-C3 | Human check (intruder + self-label) as report only (proposed), or a gate on the user's install? | **decide** ( C6.4) |
| OQ-C4 | Messengers and mail default to `drop-title`: too strict? | **decide** |
| OQ-C5 | 90-day fit window, 10k cap, 5-min activity split: fine as defaults? (G7 reports sensitivity either way) | **decide** |
| OQ-C6 | `winsqlite3.dll` as a supported dependency (documented? stable across Windows updates?) vs `e_sqlite3` bundled with `Microsoft.Data.Sqlite`; either one under Native AOT | W0 spike |
| OQ-C7 | Memory-mapped model views: counted in private bytes or not (Task Manager / `GetProcessMemoryInfo` `PrivateUsage`)? | A1 measurement |
| OQ-C8 | `GetWindowText` on elevated / hung windows from a medium-integrity agent: never blocks, returns the title? | A1 |
| OQ-C9 | Chromium's cost of an attached UIA client (accessibility tree on): CPU / RAM delta with reads on vs off | A1 (C2 acceptance 2) |
| OQ-C10 | Private-window detectors per browser (title marker or profile-button name) | A1 probe per browser |
| OQ-C11 | `KILL_ON_JOB_CLOSE` + `CpuRate` + job memory limit on the worker, created suspended; EcoQoS on a child | W0 / L1 |
| OQ-C12 | Media idle via WASAPI session enumeration for the foreground process (cost, reliability) | A1 |
| OQ-C13 | `ApplicationFrameHost` → hosted app resolution in `AppIdentity` (A's class) | A1, with A |
| OQ-C14 | Python 3.13 embeddable + hdbscan / scikit-learn wheels: final bundle size after stripping; first-run cost from the MSIX folder | WS9 |
| OQ-C15 | sklearn `HDBSCAN` vs standalone `hdbscan` speed on real extracts (Boruvka vs sklearn's tree), and DBCV cost at 3k | L1 |
| OQ-C16 | `mode=ro` readers (API, CLI export) on a WAL database the agent writes | L1 |
| OQ-C17 | Tokenizer on non-Latin scripts (CJK has no spaces) | L1 report |
| OQ-C18 | Notification listener in the portable build: impossible without package identity? (keeps C1.5 limited) | A1 |
| OQ-C19 | G6b threshold 0.05 and the 30% "That's wrong" limit: right starting constants? Replay on the user's first 30 days, pre-registered before looking (as B's OQ-B14) | L2 |

## Changes for the catalog
_(for the parent session to merge into `01-catalog.md`, `00-frame.md`, `WORKSTREAMS.md` and the shared-core notes; drafted now, final at signing)_
**Renamed / reshaped**
- C2: element cached per window, reads off-thread; per-browser cost guardrail (C2.4); private-window detector per browser or that browser is app-only.
- C3: three rule strengths, Private bucket, redaction list; **"What Tasktor knows"** view with Hide a word (C3.6).
- C4: four levels (segment, activity, workflow window, session).
- C5: df ≥ 3 → LSA 64 → PCA 16; workflow model HDBSCAN with NMF report-only; "a job was running" feature from B.
- C6: G0 data gate and **G6b information beyond app** (null-model test); C6.4 Human check (folds the proposed intruder-check row; report only).
- C7: **That's wrong** feedback (per-cluster guardrail); Hide a word.
- C8: "centroid matching" → **member-overlap matching**; adaptive cadence (7 → 28 days) + drift-triggered early refit; fit cap 10k; kill-on-close Job Object; rollback.
- C9 = X1: fallback chain workflow → activity cluster → app, always labelled learned / default.
- C14: Parquet → later; CSV / JSONL + read-only Python API in v1.

**Moved / folded**
- C13 → **H14 Self-use counts** (Core, A1).
- Proposed intruder-check row → C6.4.

**Cross-document changes**
- `00-frame.md` C1.2: Rust / agent-desktop dependency wording → C# hooks; agent-desktop is a reference and a dev-time probe (D-C14). Title hook scoped to the foreground process; no window open / close hooks in C.
- `00-frame.md` C1.6: assignment in C#, event-driven, mapped model, floor 0.5, hysteresis 2.
- `00-frame.md` C2.2: features computed in C#; resource profile cut; PCA 16 density step.
- `00-frame.md` C2.6: "centroid matching" → member-overlap matching.
- `00-frame.md` privacy rules: add 5b "Workflow models keep only hashed token buckets, numbers, app ids and words that recur (≥ 5 activities on ≥ 3 days); rare words expire with raw events after 30 days; the learning worker never sees a window title". Rule 2 gains "no keyboard or mouse hooks; UI Automation reads only the browser hostname".
- `00-frame.md` Stack: "embeddable package or a frozen PyInstaller build, decided in WS9" → embeddable (WS9 still sizes it).
- `WORKSTREAMS.md`: WS3 builds `WinEventSource` for C **and** F; WS6 owns the training extract, the model contract and the Python reference; WS9 bundles Python 3.13 embeddable.
- A's D-A3 table row "Learning (C2) … online assignment is Rust (Frame C1.6)" → C#.

## Consolidated at signing: architecture decisions and what they change elsewhere (2026-11-04)
Section C is signed. This session does **not** edit the other sections. Each row below is a change their owner should make or decide. "Must" = C depends on it; "can" = optional reuse. At signing, every  item in this file became a decision. Open questions keep their proposed answer as the working default until a probe or the owning section settles them.

**C's architecture decisions in one place**
| Id | Decision |
|---|---|
| D-C1 | Three levels (segment → activity → workflow window) and a fallback chain workflow → activity cluster → app |
| D-C2 | T0: two stores (`events.db` 30 days, `episodes.db` kept); the refit worker reads only a numbers-only training extract |
| D-C3 | Learning split: online in C# Core, batch fits in a Python worker with a parity reference |
| D-C4 | Long-term text = keyed hashes + frequent-term dictionary (≥ 5 activities on ≥ 3 days) |
| D-C5 | One learning framework with D-B9: learned value + fallback + trust ramp + guardrails + null model on every fit |
| D-C6 | `prereg.json` holds every threshold; its hash is stamped into each model |
| D-C7 | T0 fits inside D-A3: ≤ 2 MB extra in the agent; off = zero cost |
| D-C8 | T0 capture is event-driven and scoped; T0 has no keyboard or mouse hooks |
| D-C9 | Stores on Windows' `winsqlite3.dll` (FTS5 confirmed), one store-writer thread |
| D-C10 | The model is a file contract (memory-mapped sidecars), assigned online in C# with a 0.5 floor and 2-result hysteresis |
| D-C11 | Embeddable Python 3.13 worker in a kill-on-close Job Object; never resident |
| D-C12 | Stable cluster ids by member-overlap Hungarian matching |
| D-C13 | Type design: no new interface, block kind or assembly in A's sense; new sources implement existing interfaces |
| D-C14 | agent-desktop is a reference and dev-time probe; C's own UIA reader adopts its snapshot / ref shape |
| D-C15 | Tracking is a Module, off by default; learning is on once tracking is on |
| D-C16 | No-screenshot rule: structure first (events → UIA / extension → media), pixels last and gated |
| D-C17 | Actor attribution (human / agent / cli / unknown) from injected flags + agent activity + process tree, with evidence bits; never guesses a name |
| D-C18 | Screen text = event-driven UIA snapshots with stable refs + line diffs; browser via a companion MV3 extension; no CDP |
| D-C19 | Key stream: LL hook → fixed ring → key runs; field value diff is the truth; suppression (password, secure fields, excludes) + redaction before any write; 7-day raw |
| D-C20 | Unstructured representation = activity documents per track; embeddings + structural features; gate G8 vs TF-IDF |
| D-C21 | T1 runs in `tasktor-capture` (≤ 40 MB, ≤ 0.5% CPU) + a batch content worker; the agent budget is unchanged |
| C13 | Self-use counts move to H (H14) |

**A (system monitor): changes needed**
| # | Change | Kind |
|---|---|---|
| A-C1 | `AppIdentity` resolves `ApplicationFrameHost` windows to the hosted app, and maps SMTC AUMIDs to app ids | must |
| A-C2 | D-A3's row "online assignment is Rust" → C# | must |
| A-C3 | D-A5 §6 placeholders `ForegroundEvents` / `IdleEvents` become `WinEventSource` / `IdleSource` (named in C) | must (rename) |
| A-C4 | `IProcessControl` gains **`StartWorker`, `CancelWorker`, `SendControl`**. `SendControl` generalises B's `RequestPause` to a pipe message for Tasktor's own children. No `GuardChain` for Tasktor's own children | must (project-level; H review) |
| A-C5 | Capture health as `MetricId` rows (`capture.walk_p95`, `capture.ring_drops`, …) | can |

**B (long-job watch): changes needed**
| # | Change | Kind |
|---|---|---|
| B-C1 | `KindSignature` → shared `KeyedHasher(domain)` (same install key; prefixes `kind|`, `tok|`, `ref|`) | must |
| B-C2 | `GuardrailSet` → `GuardrailSet<TSubject>` | must |
| B-C4 | B5 shows refit and content-worker rows (`WorkerProgressSource`, stage names unchanged) | must (already in B) |

**G (surfaces):**
- The workflow card uses existing blocks plus a ▶ now-playing line and an actor split.

**H (platform):**
- The `Tasktor.Capture` and `tasktor-nmh` hosts.
- SQLCipher bundled in the capture process and content worker only (OQ-C22).
- C13 → H14.
- Review of the three `IProcessControl` members. *(must)*

**Frame (`00-frame.md`):**
- Privacy rule 2 becomes tiered: T0 keeps the never-recorded list, T1 follows D-C22, T2 is a separate opt-in.
- Rule 3 (Modules off by default) holds for T1, which also gets a separate consent screen.
- Rule 5a: T1 records only the signed-in user who turned it on, and it cannot run hidden. *(must)*

**Still open (probes, not decisions):**
- A1 probes:
  - MSLLHOOKSTRUCT injected flag value;
  - command-line read with limited access;
  - LL hook delivery when an elevated window is in front;
  - Electron renderer accessibility cost (OQ-C27);
  - capture memory and CPU against D-C21.
- L1: embedding model (OQ-C21) and gate G8.
- Every OQ-C row keeps its proposed default.
