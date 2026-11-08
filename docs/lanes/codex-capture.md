# Capture lane report
## Current update (2026-11-08)
This update supersedes the historical status and requests below.
- IN PROGRESS #2: the session-notification adapter is registered on the indicator window and closes the recorder gate synchronously. Registration failure prevents capture startup. Generation checks reject stale session queries and queued browser or native-source events across a notice. The existing media logic now has loop entry points for metadata, peaks, audio stop and source removal; these are tested with synthetic inputs. Native visibility, UIA and SMTC/WASAPI producers are still absent; synthetic geometry timing (30 windows, 2 monitors, 1,000 samples) was 0.576 ms p95 and 10.875 ms maximum against the <1 ms target; this includes PowerShell overhead and excludes native enumeration, so end-to-end timing is unverified and the source remains detached; the owner-run dead-key script is present and its build-only check passed, but the owner has not run its interactive check; the keyboard hook remains detached pending the owner's dead-key check. Prior blocker statements below about the missing Core shutdown result are historical: 2c98548 provides it.
- PASS: baseline .NET suite and 110 worker tests. After each code commit, formatter, warnings-as-errors solution build and full .NET suite passed; the rebased baseline capture suite passed 228 tests. Indicator-only PC probe passed startup and delivered one synthetic session notice through the actual window procedure, with capture and hooks off. Lock, unlock, UAC and real notification delivery have not been verified.
- PASS, synthetic privacy regression with a conservative mitigation: history read during a secure tick could remain pending and be recorded after reopening. Upstream now discards pending commands and the first batch read after a closed period, then resumes history. Capture also uses the session generation so a concurrent notice cannot consume a newer re-baseline, and starts with re-baselining enabled before the first safe tick. Real lock, unlock and UAC behavior remains unverified.
- PARTIAL #4 on Windows with --no-browser and a disposable history file: two synthetic file appends produced two history records; the synthetic token was redacted and the prior line absent. Forget removed both records. An app-exclusion check produced zero records. This directly appended fixture lines and checked the vault, so it did not establish interactive PSReadLine or export behavior.
- PASS partial #4 rewritten-file retest on adc5d0d: deleting and recreating the fixture produced one new history record and no record from the fixture's prior line; the record was forgotten and the fixture removed. This supersedes the earlier first-command miss. PASS on 948c136: the original synthetic pre-start restore probe returned zero records; the restored file write time was 574 ms before tail start. On d5be522 the main lane removed the previous-read allowance: retests returned zero for a file written 577 ms before tail start and zero for a separate file written 514 ms before the previous read. A fresh rewrite of synthetic content that existed before tail start returned one record; its file write was 226 ms after start. That is the documented content-provenance limit, not evidence of pre-start file-write import. All probe files were removed.
- PASS #4 synthetic shutdown proof: with a blocked real CaptureSession loop and a fake indicator, a stop failure kept the indicator active; after the loop exited, retry stopped the session and removed the indicator.
- NOT RUN #4: interactive PowerShell 7 and 5, export/activity document, forget-by-app or site, capture-off refusal and real-process indicator ordering. The capture process ended after a terminal interrupt, but the exit result did not prove normal stop or indicator ordering. Windows computer-use policy prohibits terminal UI automation. No real history file was read or modified.
- Requests to main: the main lane made both file-write time boundaries strict; the synthetic probes above show the measured boundaries and content-provenance limit. Main reviewed the generation and startup guards; both are merged upstream. Issues #2, #4 and #5 remain open; work continues.



## Issue 5: switcher checks
- PARTIAL 1: a non-UI harness used the real SwitcherFeedFeature, HostSupervisor and Windows process control with an isolated store, unique pipe and a PID filter matching no windows. The feature started its own shell process and disabling it stopped that process. Consent was supplied programmatically and the full production agent composition and consent dialog were not exercised. An earlier standalone shell smoke also stayed up for three seconds and was force-stopped.
- NOT RUN 1 (full module start/stop): upstream now filters the core foreground-event source before reading titles in check mode (ddfbf45). The full production check agent and UI consent remain untested.
- NOT RUN 2 (open/order), 3 (quick tap), 4 (cycle), 5 (search), 6 (app view), 7 (close), 8 (frozen job), 9 (DPI), and 10 (hotkey conflict): these require interactive or visual verification. A Notepad launch restored an existing session rather than a synthetic window; it was not used for the check.
- PARTIAL disk check: the harness store had zero switcher-named files; this does not prove that no other-named file was written. The temporary store was removed.
- Validation on ddfbf45: one full run failed Core test A_vanished_or_unusable_folder_never_throws_and_the_watch_recovers (expected one progress row, observed two); Capture passed 228/228 and Agent passed 66/66. The failure needs investigation by main; it is outside the capture lane.
- Baseline: the first full .NET run exited nonzero; its failure details were lost to output truncation. A full no-build rerun passed; the later rebased baseline also passed. Worker tests passed 110/110. Do not treat the first failure as explained.
## Done
- Rebased the clean capture branch onto upstream b9d259f. The committed history read, forget overload and recorder secure gate are present. No prior uncommitted integration or stash was present in this checkout.
- Media session logic: sensitive metadata, explicit playback states and the three-of-five audible window are implemented and tested. This is pure logic, not an SMTC or WASAPI source, and is not wired.
- Bounded UIA walk logic: traverses synthetic source trees with 400-node and depth-12 caps, rejects unknown/offscreen nodes and never requests values for password or unknown-password fields. It returns a Sensitive<UiaWalk>; it is not wired to Windows UIA.
- Visibility geometry: pure z-order occlusion and monitor-union calculation returns the existing VisibleSet; it is not a Windows event source and is not wired.
- Secure-desktop policy and probes: unknown desktop/session state suppresses capture by default; WTS snapshots and notification policy are testable and fail closed. These components are not yet connected to the host message loop or sources.
- Parallel uncommitted work: native media/UIA coordinators and capture-loop scaffolding. These are incomplete adapter work and are not in the pushed commits. Native UIA and WASAPI sources and session notifications are not complete. Upstream now wires browser text and media to DeepCapture; secure-desktop gating of those callbacks still needs correction.
- Privacy prerequisites (capture: protect uncertain key runs): key-run suppression receives an explicit monotonic-to-wall-clock mapper, and an unknown password state suppresses text and field-value retention. Existing synthetic safe-field fixtures now explicitly declare a non-password state.
- Low-level hook adapter (capture: low-level hook adapter): one Windows message-loop thread installs both low-level hooks and runs the 30-second inactivity watchdog. Callbacks copy typed native events to the ring and call the next hook. Keyboard/mouse flags and 32-bit tick wrap are kept distinct. The hook and layout adapter are not yet wired into the host.
- Merged upstream browser T1 events. Step 6 must route TextSeen to DeepCapture.Walk with DiffSource.Extension and MediaSeen to DeepCapture.Media on the logic thread; the M12 browser T1 real-PC check remains outstanding.
- Host wiring (capture: gate host startup): consent is checked first; the indicator gate starts the capture lifetime only after indicator creation, and recording sources drain before indicator removal. Browser-pipe shutdown now waits for active sessions.
- Indicator adapter (capture: Win32 recording indicator): capture-owned tray ring and two-pixel monitor-top windows; display and DPI messages rebuild edges, and failure is surfaced to the host. The host wiring is described above.
- Rebased the lane on the updated upstream base, then reconciled the previously pushed report history without a force push.
- Added an indicator startup gate and synthetic tests. If stopping capture throws, the gate keeps the indicator alive and permits a retry. The gate now owns the capture host lifetime.

## Checks run
- PASS: `dotnet format Tasktor.Linux.slnf`; `dotnet build Tasktor.sln -warnaserror` completed with 0 warnings and 0 errors.
- PASS: baseline `dotnet test` on b9d259f; all projects passed, including 209 Capture and 501 Core tests.
- NOT RUN: issue 2 browser, secure-session and search checks; native sources remain unattached and the shutdown guarantee below is unresolved.
- NOT RUN: issue 4 history tail, PowerShell 5, blocks, history rewrite, forget by app, forget by site and capture-off checks; no isolated live capture fixture was established. The specified history rewrite deletes the real history file and must use a disposable history fixture or an owner-controlled procedure.
- INCONCLUSIVE: real-PC layout check found US loaded but not German or French. US synthetic translation and synthetic uncertain-run suppression passed; the probe did not change the active layout or keyboard state, install hooks, log text, or persist input. Native German/French dead-key behavior and target-app behavior were not checked. The hook remains unattached under the stated gate.
- PASS: seven synthetic media-logic tests on the merged tree and local versioned Windows target; Linux execution remains unverified.
- PASS: six bounded UIA walk tests on the merged tree and the local versioned Windows target. Linux execution remains unverified.
- PASS: nine synthetic visibility tests cover overlapping windows and monitors, negative coordinates, gaps, filtering, unknown identities and the 30-window cap. No real window-source check yet.
- PASS: 205 Capture tests on the in-progress versioned Windows target, including the default-suppression and synthetic WTS/notification cases. A read-only PC probe reported an unlocked connected session on the user desktop; no lock, UAC, disconnect or notification-delivery test was performed.
- PASS: post-merge full .NET suite on the pre-retarget tree: Core 468, Capture 180, Agent 51. Added fake layout outcomes suppress an entire uncertain run, including field diffs; event suppression uses an injected wall-time mapping and unknown password status suppresses text.
- PASS: full `dotnet test Tasktor.sln` after upstream watcher fix df84e7b: Core 468, Capture 180 and Agent 51 passed; all other projects passed. The suite included the uncommitted in-progress adapter tests.
- HISTORICAL FAIL (fixed upstream): `dotnet test Tasktor.sln` aborted the Agent test host after 23 Agent tests passed: an unhandled `DirectoryNotFoundException` arose in `JobProgressSource.Rescan` (`ProgressFiles.cs:162`), called by its FileSystemWatcher error callback (line 142), while a temporary jobs directory was unavailable. Core 466 and Capture 170 tests passed before the host crash. The Release solution build passed with zero warnings and the worker suite passed 108 tests. A later post-fix full suite passed.
- PASS (parallel work, not all committed): synthetic checks included 26 secure-desktop tests, 17 session-policy tests, 9 visible-window tests, 7 media tests, 6 bounded UIA-walk tests and 10 conservative dead-key tests. These checks do not establish native adapter completeness.
- PASS: 170 Capture tests, including regressions for wall-clock suppression and unknown password state, plus the uncommitted parallel adapters. Native UIA and the host clock mapper remain to be integrated.
- PASS: isolated hook probe: with the capture-owned indicator started first, keyboard and mouse hooks installed and ran for three seconds with no reported failure; the process kept event contents only in memory and printed none. No synthetic key, watchdog-removal, or secure-desktop test was performed.
- PASS: full .NET suite (including parallel uncommitted visibility, media and desktop-guard tests): Core 466, Capture 125. Browser extension unit tests 8 passed.
- PASS: isolated capture host smoke test: without consent, exit code 3; with consent in a temporary store, the process stayed alive for five seconds after indicator-gated startup. The process was stopped and the store deleted. No hooks are installed yet.
- PASS: full .NET suite, including 79 Capture tests; synthetic tests cover indicator refusal, loss before start and a browser callback held open during server disposal.
- PASS: indicator-only probe, no capture: startup succeeded on a two-monitor PC; two visible edge windows matched both monitor top bounds and were exactly two physical pixels high. Tray registration returned success. Physical monitor changes, DPI changes, and visual tray appearance remain to be verified.
- PASS: full .NET suite (Core 463, Capture 75); full Release solution build with zero warnings; Python worker 108 passed. The Capture suite was rerun after the DPI-awareness failure guard and passed 75 tests.
- PASS: `dotnet build Tasktor.sln -c Release -warnaserror` on the updated base: 0 warnings, 0 errors.
- PASS: `dotnet test Tasktor.sln` after upstream StoreWriter fix 96c9068: all projects passed, including Core 459/459 and Capture 74/74. The previous writer shutdown failure did not recur.
- PASS: `py -m pytest worker --basetemp=<fresh-temp-dir> -q`: 108 passed on 96c9068.
- PASS (partial), M14 shelf: with a fresh `--data-dir` under `%TEMP%`, the agent ran and the shelf displayed `All quiet. No jobs.` and `Everything Tasktor knows`. The deep link opened a window. No screenshot was saved.
- PASS (partial), M14 deep window: the shelf link opened Home with the seven default navigation entries: Home, System, Jobs, Activity, What Tasktor learned, Your data, Settings. Selecting System exposed System, Apps, Battery, and Guards content in the accessibility tree.
- PASS, scheduler fix (upstream cfe10a9): after starting the Release agent with a fresh temporary data directory, `tasktor status` showed numeric CPU and RAM and populated app rows at 15 seconds. Within the first minute the deep window System page also showed numeric CPU and RAM and a nonempty Top apps table. This supersedes the earlier empty-metrics observation. Test processes were stopped and the temporary data directory removed; capture was not enabled.
- SKIPPED: remaining shelf checks (multiple scaling settings, auto-hide, warn/destructive actions, Narrator, exit timer and memory), tray + edge, command line, theme switching, and lifecycle lease: not yet completed. The Windows automation tool prohibits Windows-key shortcuts; lifecycle needs a WinEvent spy and a configured preset.
- SKIPPED: M13 render fixtures; an offline widget host or designer has not been established.
- SKIPPED: admin-dependent checks, second-account checks, firewall and audit-policy changes, browser-extension installation, and checks requiring the user at the keyboard, as instructed.
- The test agent and flyout were stopped, and the isolated data directory was removed. Capture was not enabled.

- PASS: four synthetic startup gate tests, including a capture-stop failure and retry; native indicator and host wiring were subsequently added.

- IN PROGRESS: secure-desktop, visible-window and media logic have synthetic tests but are not yet integrated. Upstream wires the browser T1 events to DeepCapture; remaining source and session wiring is outstanding.

## Interpretations
- Upstream's recorder write gate addresses the previously reported in-flight write gap; notification integration and its real-PC verification remain outstanding.
- Unsupported dead-key compositions suppress the entire key run; the Windows layout adapter remains unwired as text decoding until the real-PC check.
- The specified aggregate watchdog signal cannot detect a single removed hook while the other continues receiving events. This is a known limitation; no additional input source is introduced.
- The amended D-G16 indicator is the top edge of every monitor. The first indicator must fail closed before hooks are installed.

## Needs Windows verification
- Issues 2 and 4 remain open: perform their listed PC checks using synthetic content and a disposable history fixture, then forget captured data.
- Implement and verify SMTC plus WASAPI session notifications, AUMID-to-app resolution and audio-only activity without inventing playback status. No real media session was read by the pure-logic tests.
- Supply and verify a native UIAutomationCore backend with a real one-second transaction timeout, safe password property-read ordering, and bounded name/automation-ID reads. The pure walker caps values but does not cap those two strings. No real UIA walk was exercised.
- Finish every unchecked M14 shelf, theme, tray + edge, command line, deep window and lifecycle step in `docs/PROGRESS.md` with an isolated temporary `--data-dir`; remove the directory afterward.
- Run the M13 render fixtures only if an offline widget host or Adaptive Cards designer is available.
- Exercise monitor add/remove and mixed DPI changes, tray appearance and Explorer restart with an isolated capture session; verify fail-closed shutdown if either indicator fails. Then verify hooks, secure desktop, UIA and media sources once implemented.

## Requests
- Core test: please investigate the intermittent progress-file recovery failure recorded above; the earlier full baseline passed.

- Issue 5: thank you for filtering WinEventSource in ddfbf45. The production check agent still samples real process names and resource metrics. Could check mode suppress non-fixture process sampling or offer a synthetic process provider? The owner requested synthetic sensitive data and prefers command-line or API checks where possible. Interactive and visual behavior remains unverified.

- History-transition and no-browser fixture requests were answered upstream. The strict timestamp boundaries and content-provenance limit are recorded above. The prior Core shutdown request below is resolved by 2c98548.

- Please provide the Core loop shutdown completion guarantee requested below. Also advise a supported disposable history-path fixture for the real-PC history rewrite check; the documented command otherwise deletes the owner's actual history.
- Core shutdown observability: expose a bounded stop/join result or completion task from `Tasktor.Core.Loop.LogicLoop` that is true/completed only after its thread exits, and test a blocked callback. CaptureSession cannot prove thread termination before indicator removal through the current void Stop API. Core is outside this lane; do not wire sources as if this guarantee exists.
- Jobs watcher crash fixed upstream in df84e7b; post-merge full suite passed.
- StoreWriter shutdown race fixed upstream in 96c9068; no open request for that issue.

## Integration in progress
- Historical: the prior uncommitted notification integration is absent. The upstream write gate addresses the reported write race; notification integration remains unverified.
- Native UIA and media backends are incomplete; the visibility snapshot has not met the timing target. None is attached.
- Upstream through b9d259f is now in this branch; the encrypted index and host changes are present.

## Blockers
- Safe source attachment is blocked on the Core loop shutdown completion guarantee: Stop performs a timed join but does not expose whether the thread exited. The previously described uncommitted native integration is absent from the checkout and stash; native UIA and media sources remain to be implemented. Issues 2 and 4 have not been completed.
- Historical write-race blocker: upstream added a recorder write gate. Notification delivery is not wired or verified.
- The full real-PC dead-key gate is inconclusive because German and French layouts were not loaded; do not attach the hook until the requested check passes. Loading a layout can activate it system-wide, so the probe did not change the user's keyboard configuration.
- No current code-test blocker. Native UIA, WASAPI, secure-session notification wiring, and browser/DeepCapture wiring remain implementation work.

- The Windows layout adapter remains unwired pending real-PC verification; this is an implementation status, not a blocker.

- Windows computer-use policy disallows Win-key shortcuts, so the Win+Alt+T shortcut itself cannot be automated in this session.










