# Progress

Update this file after every milestone step. The next session starts here.

## Current milestone
M1 Contracts (WS0). M0 is done.

## Done
- Design signed (`docs/design/`).
- Implementation plan (`docs/IMPLEMENTATION.md`).
- **M0 Scaffold.** `Tasktor.sln` with every project in the layout; `Tasktor.Linux.slnf` (everything except Flyout / Widget) for Linux; `Directory.Build.props` (nullable, warnings as errors, latest language, `EnableWindowsTargeting`, invariant globalization); central package versions in `Directory.Packages.props`; one xUnit test project per src project (`tests/Directory.Build.props` adds xUnit); `ci.yml` builds and tests the filter on `ubuntu-latest` and the whole solution on `windows-latest`. G-net is wired from day one: every `src/` project bans `System.Net*` through `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`src/BannedSymbols.txt`, error RS0030); verified by compiling a probe `HttpClient` in Core.

## Next
- M1: contracts as C# records, STJ source-gen context, SchemaGen, fixtures, G-schema round trip (C# → JSON → Python `jsonschema` → C#).

## Interpretations
- **Linux build set.** CI's Linux job builds `Tasktor.Linux.slnf`, a solution filter without `Tasktor.Flyout` and `Tasktor.Widget` (WinUI / Windows App SDK do not build on Linux). The Windows job builds `Tasktor.sln`.
- **`net10.0-windows` projects run on Linux.** `Tasktor.Windows`, `Tasktor.Agent`, `Tasktor.Capture` and `Tasktor.Shell` target `net10.0-windows` (compiled on Linux with `EnableWindowsTargeting`); their test projects target the same TFM and run on Linux. Only code paths that call Win32 need Windows; everything else is tested here.
- **G-net from M0.** The banned-API analyzer (planned for M12/M15) is on from the start for every `src/` project; it costs nothing and stops accidents early. Network use lives only in plugins (outside `src/`).
- **No Flyout / Widget test projects yet.** Their testable logic lives in `Tasktor.Hosts.Common` (renderers, fitter), which has its own test project.
- **Package pins.** xUnit 2.9.3, Test SDK 17.14.1, BannedApiAnalyzers 4.14.0, Windows App SDK 2.5.1 (the version the U4 sources cite).

## Needs Windows verification
- **M0 / CI:** the `windows` job of `ci.yml` builds `Tasktor.sln` including the WinUI 3 Flyout and the Widget project. Check: the job is green on GitHub Actions. If the Flyout fails with a platform error, build with `-p:Platform=x64`.

## Blockers
- **.NET install script blocked.** `dotnet-install.sh` cannot reach `builds.dotnet.microsoft.com` (the session proxy returns 403). Worked around: `apt-get install dotnet-sdk-10.0` from the Ubuntu archive (SDK 10.0.114, which satisfies `global.json` 10.0.100 / latestFeature). `scripts/cloud-setup.sh` now falls back to apt. Not blocking.
