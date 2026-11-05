# Tasktor

A lightweight Windows 11 companion for people who run long jobs on their PC.

- **System monitor:** CPU, RAM, GPU, disk and network, grouped by app, with a one-line "why is it slow" verdict.
- **Long-job watch:** spots long-running jobs (training runs, builds, scripts), shows progress and ETA, and can pause, lower or resume them.
- **Workflow learning:** optionally learns how you work, on your PC only, and reports time per workflow.

Everything runs locally. The core agent makes no network connections.

## Status

Pre-alpha. The design is complete and the code is not written yet.

## Planned pieces

- `tasktor-core`: a small Native AOT agent with a tray icon.
- A flyout window, a Windows Widgets board widget (MSIX build) and a CLI (`tasktor status --json`).
- Plugins: local programs that add cards, under a documented contract.

## License

Apache-2.0. See [LICENSE](LICENSE).
