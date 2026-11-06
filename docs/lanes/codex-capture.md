# Capture lane report

## Done
- Rebased the lane on the updated upstream base, then reconciled the previously pushed report history without a force push.
- Ran the baseline and partial isolated Windows UI checks below. Capture adapter implementation remains outstanding.

## Checks run
- PASS: `dotnet build Tasktor.sln -c Release -warnaserror`: 0 warnings, 0 errors.
- PASS: `dotnet test Tasktor.sln`: all test projects passed; Core 436/436 and Capture 71/71. The former symbolic-link privilege failure did not recur.
- PASS: `py -m pytest worker --basetemp=<fresh-temp-dir> -q`: 108 passed.
- PASS (partial), M14 shelf: with a fresh `--data-dir` under `%TEMP%`, the agent ran and the shelf displayed `All quiet. No jobs.` and `Everything Tasktor knows`. The deep link opened a window. No screenshot was saved.
- PASS (partial), M14 deep window: the shelf link opened Home with the seven default navigation entries: Home, System, Jobs, Activity, What Tasktor learned, Your data, Settings. Selecting System exposed System, Apps, Battery, and Guards content in the accessibility tree.
- OBSERVATION, outside lane: after the agent had run for several minutes, both the deep window and `tasktor status` showed CPU and RAM as unavailable and an empty app table, although the CLI connected and battery read 100%. Repro: start the Release agent with a new temporary data directory, run `tasktor status`, inspect System. No diagnosis established; agent and sources are outside this lane.
- SKIPPED: remaining shelf checks (multiple scaling settings, auto-hide, warn/destructive actions, Narrator, exit timer and memory), tray + edge, command line, theme switching, and lifecycle lease: not yet completed. The Windows automation tool prohibits Windows-key shortcuts; lifecycle needs a WinEvent spy and a configured preset.
- SKIPPED: M13 render fixtures; an offline widget host or designer has not been established.
- SKIPPED: admin-dependent checks, second-account checks, firewall and audit-policy changes, browser-extension installation, and checks requiring the user at the keyboard, as instructed.
- The test agent and flyout were stopped, and the isolated data directory was removed. Capture was not enabled.

## Interpretations
- The amended D-G16 indicator is the top edge of every monitor. The first indicator must fail closed before hooks are installed.

## Needs Windows verification
- Finish every unchecked M14 shelf, theme, tray + edge, command line, deep window and lifecycle step in `docs/PROGRESS.md` with an isolated temporary `--data-dir`; remove the directory afterward.
- Run the M13 render fixtures only if an offline widget host or Adaptive Cards designer is available.
- All six M12 capture adapter items and their real-PC checks remain outstanding.

## Requests
- None yet.

## Blockers
- Windows computer-use policy disallows Win-key shortcuts, so the Win+Alt+T shortcut itself cannot be automated in this session.
