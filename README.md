# Tasktor

A lightweight Windows 11 companion for people who run long jobs on their PC.

- **System monitor:** CPU, RAM, GPU, disk and network, grouped by app, with a one-line "why is it slow" verdict.
- **Long-job watch:** spots long-running jobs (training runs, builds, scripts), shows progress and ETA, warns when one goes
  silent, and can pause, lower or resume it.
- **Sources and guards:** next event and deadlines from your calendars, meeting and battery guards, AI-agent watch, folder
  actions, connectivity, a space report, a focus timer with notes.
- **Workflow learning (Module, off by default):** learns how you work, on your PC only, and reports time per workflow.

## Privacy, in plain words

- **The agent never connects to the network.** No telemetry, no crash upload, no update ping. Only plugins you turn on can
  use the network, and each one says where (the calendar plugin fetches the addresses you add; nothing else does).
- **Nothing leaves the PC.** Your data lives under `%LOCALAPPDATA%\Tasktor`. `tasktor export data` writes it to a folder you
  choose, as plain CSV / JSONL; `tasktor forget …` deletes it.
- **Personal data is never logged.** Window titles, command lines, paths under your profile, URLs and typed text are kept out
  of the log by type (`Sensitive<T>`), and a CI gate checks it.
- **Workflow tracking** records apps, redacted window titles and browser host names only, never keystrokes, clipboard, file
  contents or full URLs. Password managers, private browser windows and credential prompts are excluded by default.
- **Deep capture (T1)** is a separate opt-in with its own consent screen and an always-visible indicator; its binary holds no
  network code (CI gate G-capture-net), so nothing it records can leave the PC by itself.

## Install (portable build)

1. Download `tasktor-<version>-win-x64.zip` and `SHA256SUMS.txt` from a release, and check the hash:
   `Get-FileHash .\tasktor-<version>-win-x64.zip -Algorithm SHA256`.
2. Unzip anywhere and run `tasktor-core.exe`. It adds a tray icon; `tasktor status` in a terminal shows the same data.

### SmartScreen and antivirus

Releases are **unsigned** (signing is being attempted, never a blocker). Windows SmartScreen may say "Windows protected your
PC": choose **More info → Run anyway** after checking the hash. Some antivirus products flag any program that installs a
keyboard hook; only `tasktor-capture.exe` (deep capture, off by default) does, and nothing else in Tasktor carries a hook.
Every release is built from a tag by CI (`.github/workflows/release.yml`), so you can rebuild the same binaries from source.

## Plugins

A plugin is a folder under `%LOCALAPPDATA%\Tasktor\plugins\<id>\` with a `plugin.json` manifest (schema
`schemas/tasktor.plugin-1.json`) and a program. Tasktor runs it on its interval, one process per run inside a Job Object
(256 MB, a timeout), passes one JSON object on stdin and reads up to 3 cards from stdout, built from a fixed subset of
blocks with literal values. The manifest declares what it reads, which network hosts it uses and its actions; turning it on
shows that in a consent dialog, and the consent is pinned to the files' hashes. See `examples/plugins/todo-count/`, and the
first-party plugins in `plugins-builtin/` (`tasktor.calendar`, `tasktor.repos`).

Writing one: [`docs/plugins.md`](docs/plugins.md). `tasktor plugin new <id>` starts a working plugin, and
`tasktor plugin check <folder> --run` runs the agent's own checks on it without installing anything. Contributions:
[`CONTRIBUTING.md`](CONTRIBUTING.md).

## Rules and data

- Rules (`%LOCALAPPDATA%\Tasktor\rules.json`, JSON with comments) override the shipped ones by `id`: see `docs/rules.md`.
- Your data and the Python loader for exports: see `docs/data.md`.
- Long jobs can report progress through `worker/tasktor_progress.py` (stdlib only) or `tasktor job set`.

## Build and test

Requires the .NET 10 SDK (`global.json`) and Python 3.12+.

```
scripts/check.sh                       # format check, -warnaserror build, .NET tests, Python tests (Linux)
dotnet build Tasktor.sln -warnaserror  # everything, including the WinUI flyout and the widget (Windows)
dotnet test Tasktor.sln
pip install -r worker/requirements.txt && pytest worker
```

Linux builds `Tasktor.Linux.slnf` (all but the WinUI projects); Windows-only adapters compile everywhere and are checked on a
real PC (see `docs/PROGRESS.md` → "Needs Windows verification").

## Layout

| Path | What |
|---|---|
| `src/Tasktor.Contracts` | public records; JSON Schemas in `schemas/` are generated from them |
| `src/Tasktor.Core` | the logic loop, shapes, rules, actions, storage and every feature (portable, tested on Linux) |
| `src/Tasktor.Windows` | Win32 adapters behind the shared interfaces |
| `src/Tasktor.Agent` | `tasktor-core`: composition, the pipe server, shipped `rules.default.json` |
| `src/Tasktor.Cli` | `tasktor` |
| `src/Tasktor.Capture`, `Shell`, `Flyout`, `Widget`, `Nmh` | deep capture, the window shell, the viewers, the browser bridge |
| `worker/` | the Python learning worker, the reference implementations, the export loader, the progress helper |
| `plugins-builtin/` | first-party plugins (off by default) |
| `docs/design/` | the signed specs; `docs/IMPLEMENTATION.md` the milestones; `docs/PROGRESS.md` where work stands |

## License

Apache-2.0. See [LICENSE](LICENSE).
