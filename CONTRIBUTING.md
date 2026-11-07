# Contributing

## Plugins

The easiest way to extend Tasktor is a plugin in your own repository; it needs no change here. Start with
[`docs/plugins.md`](docs/plugins.md): `tasktor plugin new` gives you a working folder, and `tasktor plugin check --run`
runs the agent's own checks on it.

## Changes to Tasktor

- Read [`docs/IMPLEMENTATION.md`](docs/IMPLEMENTATION.md) and the specs in [`docs/design/`](docs/design). The specs are
  signed; a change to behaviour starts as a change to the spec.
- Build and test with `./scripts/check.sh`: `dotnet build -warnaserror`, `dotnet test`, `pytest worker` and the
  extension's tests. On Linux, the Windows-only projects build through `Tasktor.Linux.slnf`.
- Format with `dotnet format` before committing.
- The hard rules, which reviews check:
  - **the agent process never opens a network connection**, and nothing is ever sent off the PC;
  - personal data (window titles, command lines, paths, URLs, typed text) stays in `Sensitive<T>` and is never logged;
  - rules may only call reversible actions;
  - contracts are C# records, and the JSON schemas are generated from them (`tools/Tasktor.SchemaGen`), never written
    by hand;
  - the repository stays generic: no personal names, machine paths or private project names.
- Small commits with plain messages.

## Reporting a problem

Open an issue with what you did, what happened and what you expected. `tasktor log bundle` writes a log bundle you can
read before you attach it; logs never hold titles, paths or command lines.
