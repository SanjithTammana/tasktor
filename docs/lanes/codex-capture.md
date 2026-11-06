# Capture lane report

## Done
- Local uncommitted work at stop: key-run wall-clock and unknown-field privacy fixes, conservative dead-key composition, secure-desktop/WTS logic, bounded UIA walk, visible-window geometry, and media logic. These are incomplete adapter work and are not in the pushed commits. No native UIA or media adapter, session notification wiring, or DeepCapture/browser event integration is complete.
- UNCOMMITTED privacy prerequisites: key-run suppression receives an explicit monotonic-to-wall-clock mapper, and an unknown password state suppresses text and field-value retention. Existing synthetic safe-field fixtures now explicitly declare a non-password state.
- Low-level hook adapter (capture: low-level hook adapter): one Windows message-loop thread installs both low-level hooks and runs the 30-second inactivity watchdog. Callbacks copy typed native events to the ring and call the next hook. Keyboard/mouse flags and 32-bit tick wrap are kept distinct. The hook and layout adapter are not yet wired into the host.
- Merged upstream browser T1 events. Step 6 must route TextSeen to DeepCapture.Walk with DiffSource.Extension and MediaSeen to DeepCapture.Media on the logic thread; the M12 browser T1 real-PC check remains outstanding.
- Host wiring (capture: gate host startup): consent is checked first; the indicator gate starts the capture lifetime only after indicator creation, and recording sources drain before indicator removal. Browser-pipe shutdown now waits for active sessions.
- Indicator adapter (capture: Win32 recording indicator): capture-owned tray ring and two-pixel monitor-top windows; display and DPI messages rebuild edges, and failure is surfaced to the host. The host wiring is described above.
- Rebased the lane on the updated upstream base, then reconciled the previously pushed report history without a force push.
- Added an indicator startup gate and synthetic tests. If stopping capture throws, the gate keeps the indicator alive and permits a retry. The gate now owns the capture host lifetime.

## Checks run
- FAIL: latest full `dotnet test Tasktor.sln` aborted the Agent test host after 23 Agent tests passed: an unhandled `DirectoryNotFoundException` arose in `JobProgressSource.Rescan` (`ProgressFiles.cs:162`), called by its FileSystemWatcher error callback (line 142), while a temporary jobs directory was unavailable. Core 466 and Capture 170 tests passed before the host crash. The Release solution build passed with zero warnings and the worker suite passed 108 tests. The earlier full suite passed before the latest uncommitted work.
- PASS: parallel synthetic checks included 26 secure-desktop tests, 17 session-policy tests, 9 visible-window tests, 7 media tests, 6 bounded UIA-walk tests and 10 conservative dead-key tests. These checks do not establish native adapter completeness.
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

- IN PROGRESS: secure-desktop, visible-window and media logic have synthetic tests but are not yet integrated. DeepCapture and the browser T1 events remain unwired.

## Interpretations
- The amended D-G16 indicator is the top edge of every monitor. The first indicator must fail closed before hooks are installed.

## Needs Windows verification
- Finish every unchecked M14 shelf, theme, tray + edge, command line, deep window and lifecycle step in `docs/PROGRESS.md` with an isolated temporary `--data-dir`; remove the directory afterward.
- Run the M13 render fixtures only if an offline widget host or Adaptive Cards designer is available.
- Exercise monitor add/remove and mixed DPI changes, tray appearance and Explorer restart with an isolated capture session; verify fail-closed shutdown if either indicator fails. Then verify hooks, secure desktop, UIA and media sources once implemented.

## Requests
- Outside lane: investigate `src/Tasktor.Core/Features/Jobs/ProgressFiles.cs`. Ensure the FileSystemWatcher error callback and shutdown/temporary-directory cleanup cannot race into an unhandled filesystem exception; preserve legitimate overflow rescans. The full Agent test host crashed in this callback, so capture work is stopped under the requested code-test-failure rule.
- StoreWriter shutdown race fixed upstream in 96c9068; no open request for that issue.

## Blockers
- Full .NET test run aborted on the out-of-lane jobs-watcher callback described above. Do not treat the latest full suite as green; no Capture source changes after the last push have been committed.
- Windows layout translation is deliberately unintegrated: dead-key composition and explicit whole-run suppression for unsupported sequences are not yet implemented or verified. Do not wire this adapter as complete text decoding.
- The specified watchdog compares aggregate input against aggregate hook events; it cannot detect one hook being silently removed while the other still receives input. This is a limitation of the specified signal, not a verified independent-hook detector.
- The earlier StoreWriter baseline blocker was fixed upstream; it is separate from the current watcher crash.

- Windows computer-use policy disallows Win-key shortcuts, so the Win+Alt+T shortcut itself cannot be automated in this session.


