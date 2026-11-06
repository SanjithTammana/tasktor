# Capture lane report

## Done
- Created the capture lane branch from the requested base. No capture implementation was started because the required baseline is red.

## Checks run
- PASS: `dotnet build Tasktor.sln -c Release -warnaserror` (0 warnings, 0 errors).
- FAIL: `dotnet test Tasktor.sln`: `Tasktor.Core.Tests.DiskGuardTests.Folder_scan_counts_each_file_once_and_never_follows_links` failed at `SystemMonitorTests2.cs:157`; `Directory.CreateSymbolicLink` returned Windows error 1314, required privilege not held. Core: 427 passed, 1 failed. Other reported test projects passed.
- PASS: `py -m pytest worker --basetemp=<fresh-temp-dir> -q`: 108 passed. Plain `pytest worker` was unavailable on PATH; the first module run had 22 setup errors because the existing pytest temp directory was inaccessible. A fresh temp directory resolved those errors without changing tests.
- SKIPPED: M14 shelf, theme and lifecycle lease checks; baseline failed before real-PC checks could be started. No agent or flyout was launched and no capture was enabled.
- SKIPPED: M13 rendering fixtures; availability of an offline host or designer was not established before the baseline stop.
- SKIPPED: admin-dependent checks, second-account checks, firewall and audit-policy changes, browser-extension installation, and checks requiring the user at the keyboard, as instructed.

## Interpretations
- The clean-baseline gate applies before adapter implementation; a privilege-dependent test failure is still a red baseline and tests must not be skipped or weakened.

## Needs Windows verification
- Once the full baseline is green, run the M14 shelf, theme and lifecycle lease procedures in `docs/PROGRESS.md` using a throwaway `--data-dir` under `%TEMP%`; delete the data directory after the run. Record each observation.
- Run the M13 render fixtures only if an offline widget host or Adaptive Cards designer is available.
- All six M12 capture adapter items and their real-PC checks remain outstanding.

## Requests
- None.

## Blockers
- The full .NET test baseline needs an environment where the current account can create the symbolic link used by the Core test, without weakening the test. Administrative changes were explicitly excluded from this lane.
