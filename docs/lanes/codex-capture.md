# Capture lane report

## Done
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
- Unsupported dead-key compositions suppress the entire key run; the Windows layout adapter remains unwired as text decoding until the real-PC check.
- The specified aggregate watchdog signal cannot detect a single removed hook while the other continues receiving events. This is a known limitation; no additional input source is introduced.
- The amended D-G16 indicator is the top edge of every monitor. The first indicator must fail closed before hooks are installed.

## Needs Windows verification
- Implement and verify SMTC plus WASAPI session notifications, AUMID-to-app resolution and audio-only activity without inventing playback status. No real media session was read by the pure-logic tests.
- Supply and verify a native UIAutomationCore backend with a real one-second transaction timeout, safe password property-read ordering, and bounded name/automation-ID reads. The pure walker caps values but does not cap those two strings. No real UIA walk was exercised.
- Finish every unchecked M14 shelf, theme, tray + edge, command line, deep window and lifecycle step in `docs/PROGRESS.md` with an isolated temporary `--data-dir`; remove the directory afterward.
- Run the M13 render fixtures only if an offline widget host or Adaptive Cards designer is available.
- Exercise monitor add/remove and mixed DPI changes, tray appearance and Explorer restart with an isolated capture session; verify fail-closed shutdown if either indicator fails. Then verify hooks, secure desktop, UIA and media sources once implemented.

## Requests
- Jobs watcher crash fixed upstream in df84e7b; post-merge full suite passed.
- StoreWriter shutdown race fixed upstream in 96c9068; no open request for that issue.

## Blockers
- No current code-test blocker. Native UIA, WASAPI, secure-session notification wiring, and browser/DeepCapture wiring remain implementation work.

- The Windows layout adapter remains unwired pending real-PC verification; this is an implementation status, not a blocker.

- Windows computer-use policy disallows Win-key shortcuts, so the Win+Alt+T shortcut itself cannot be automated in this session.









