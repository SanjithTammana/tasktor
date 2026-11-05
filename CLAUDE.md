# Working in this repo

Tasktor is a Windows 11 system monitor, long-job watch and local workflow-learning app. The design is **complete and signed**. Your job is to implement it, test it and keep going until the milestones in `docs/IMPLEMENTATION.md` are done.

## Read first, in this order
1. `docs/IMPLEMENTATION.md`: milestones, the repo layout, what "done" means, and what can and cannot be verified on Linux.
2. `docs/PROGRESS.md`: where the last session stopped. **Always read it before doing anything, and update it after every milestone step.** It is how work survives context resets.
3. `docs/design/`, the signed specs. **The specs win** over anything you assume. Open the one for the milestone you're on:
   - `00-frame.md`: the product frame and its privacy rules;
   - `01-catalog.md`: every feature row;
   - `A-system-monitor.md`: shared core design (D-A5: four data shapes, the interfaces, cards as blocks, one logic thread);
   - `B-long-job-watch.md`;
   - `C-workflow-learning.md`;
   - `U1-platform.md`: contracts, actions, rules grammar, plugins, storage, processes, budgets, CI gates;
   - `U2-sources-guards.md`, `U3-windows.md` and `U4-surfaces.md`.

   Each spec ends with "Consolidated at signing" (its decisions in one table). Lines tagged "amended from Ux" are later changes and win over older text.

## The loop
For each milestone in `docs/IMPLEMENTATION.md`, in order:
1. Read the spec sections it names. List the classes and contracts it needs. Reuse A's shared types; don't invent new shapes or interfaces (a spec rule).
2. Write tests first where the behaviour is pure logic, then the code.
3. Run `dotnet build -warnaserror` and `dotnet test`, plus `pytest` for `worker/`. Fix until green. Never skip, disable or loosen a test to get green.
4. Commit a coherent step (see Git), and update `docs/PROGRESS.md`: what is done, what is next, open problems.
5. Move to the next milestone. If a spec is ambiguous, pick the reading most consistent with the spec's decision tables. Record it in `docs/PROGRESS.md` under "Interpretations" and keep going; don't stop to ask.

Stop only when every Linux-verifiable milestone is done and green, or when a blocker can't be worked around. Record the blocker in `docs/PROGRESS.md`.

## Hard rules (from the specs)
- **The agent process never makes network connections** (G-net). There is no `HttpClient` / `System.Net` in `Tasktor.Core`, `Tasktor.Windows` or the agent host. Network use lives only in plugins.
- **No telemetry, no crash upload, nothing leaves the PC.**
- **Personal data** (window titles, command lines, paths under the profile, URLs, typed text) is wrapped in `Sensitive<T>` and never logged (G-redact).
- **One logic thread in the agent.** Sources only enqueue; actions run off the loop and post results back.
- **Rules may only call reversible actions.** Destructive actions are two-step with a confirm token. Rules may add capture blocks but never lift them.
- **Native AOT-safe code in the agent:** no reflection-based JSON (use `System.Text.Json` source generation), no `dynamic`, no runtime codegen.
- **Contracts are C# records**; JSON Schemas are generated from them by CI, never hand-written (D-H1). Configuration files are JSON with comments.
- **Generic only.** No personal names, machine paths or private project names anywhere in the repo. Paths come from Known Folders and settings.

## Linux vs Windows
Cloud sessions run on Linux. Build everything that is platform-independent and test it fully there. For Windows-only code:
- set `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so `net10.0-windows` class libraries compile on Linux;
- keep the logic behind A's interfaces, with fakes in the test projects;
- write the Win32 adapters so they compile;
- list each item that needs a real-Windows check in `docs/PROGRESS.md` → "Needs Windows verification", with the exact check to run.

WinUI 3 (flyout), the Widgets provider and MSIX packaging do not build on Linux. Scaffold them only in the milestones that say so, and mark them for Windows verification.

## Tooling
- .NET SDK 10 (`global.json`). If `dotnet` is missing in the cloud container, install it with the official `dotnet-install.sh --channel 10.0` into `$HOME/.dotnet` and add it to `PATH`.
- Python 3.12+ for `worker/` (the shipped app bundles embeddable Python 3.13). Use `pip install -r worker/requirements.txt`; numpy, scikit-learn and hdbscan-style clustering per the C spec.
- Format with `dotnet format` before each commit.

## Git
- Work on the session's branch, never on `main`. Push your branch; open a **draft** pull request to `main` when the first milestone is green, and keep updating it.
- **Commits and PRs carry only the repo owner as author.** Do not add `Co-Authored-By` trailers, "Generated with" lines or any tool attribution to commit messages, PR titles, PR bodies or code comments.
- Small, coherent commits with plain messages ("core: RingBuffer + tests").
