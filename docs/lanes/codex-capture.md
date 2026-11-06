# Capture lane report

## Done
- Rebased the lane on the updated upstream base, then reconciled the previously pushed report history without a force push.
- Added a fail-closed indicator startup gate and three synthetic tests. The gate is not yet wired to a Windows indicator or capture host.

## Checks run
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

- PASS: three synthetic startup gate tests; the native Windows indicator and host wiring are outstanding.

## Interpretations
- The amended D-G16 indicator is the top edge of every monitor. The first indicator must fail closed before hooks are installed.

## Needs Windows verification
- Finish every unchecked M14 shelf, theme, tray + edge, command line, deep window and lifecycle step in `docs/PROGRESS.md` with an isolated temporary `--data-dir`; remove the directory afterward.
- Run the M13 render fixtures only if an offline widget host or Adaptive Cards designer is available.
- All six M12 capture adapter items and their real-PC checks remain outstanding.

## Requests
- StoreWriter shutdown race fixed upstream in 96c9068; no open request for that issue.

## Blockers
- No open baseline blocker; the full .NET suite passed after the upstream shutdown fix.

- Windows computer-use policy disallows Win-key shortcuts, so the Win+Alt+T shortcut itself cannot be automated in this session.




