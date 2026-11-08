# Your data

Everything Tasktor keeps is under `%LOCALAPPDATA%\Tasktor` (the portable build's `--data-dir` moves it). Nothing is sent
anywhere.

| Path | What | Cleared by |
|---|---|---|
| `config.json`, `rules.json`, `presets.json` | your settings and rules (JSON with comments) | `settings reset` |
| `state\` | learned job kinds, frozen-job registry, focus results, project map, connectivity drops, consents, the install key | `forget learned` / `forget tracking` / `forget all` (never the key or consents) |
| `jobs\`, `agents\` | progress files and agent hook files | stale ones expire |
| `track\events.db` | T0 events after the privacy gate (redacted titles, host names), 30 days | `forget tracking [--since]` |
| `track\episodes.db` | activities with hashed tokens, workflow windows, sessions, frequent terms, assignments | `forget tracking [--since]` |
| `track\models\` | learned workflow models (current + 2 previous) | `forget learned` |
| `capture\` | T1 deep capture (encrypted vault, 7 days) | `forget capture` |
| `notes\` | your quick notes | never by `forget`; `settings export --include-notes` includes them |
| `plugin-data\<id>\` | each plugin's own files | turning the plugin off and removing it |

## Export

```
tasktor export data [--to <folder>] [--since 30d] [--format csv|jsonl|both] [--include-raw] [--include-capture] [--yes]
```

The folder gets `manifest.json` (`tasktor.export/1`: export time, version, each table's schema id, row counts, what was left
out) and one CSV and / or JSONL file per table: `jobs_runs`, `job_kinds`, `activities`, `episodes`, `workflows`,
`workflow_time_daily`, `switches_daily`, `projects`, `project_time_weekly`, `focus_blocks`, `focus_presets`,
`connectivity_hourly`, `system_last_hour`, `alerts_log`, `actions_log`, `selfuse` (tables of Modules you never turned on
are simply absent). Raw events (window titles) need `--include-raw`, deep capture needs `--include-capture`; both ask for
confirmation because they write personal text in the clear. Exports never contain the install key, consents or plugin
secrets. A destination inside OneDrive / Dropbox / Google Drive gets a warning first.

## Python

```python
from tasktor_data import load      # worker/tasktor_data, pip-installable from the repo (worker/pyproject.toml)
tables = load("Documents/Tasktor exports/tasktor-export-20261104-0900")
tables["focus_blocks"].groupby("preset").on_intent_share.mean()
```

`load(folder)` returns `{table: pandas.DataFrame}` with time columns parsed; `load_manifest(folder)` returns the checked
manifest. It reads exports only, so it never locks the live stores.

## The learning worker's inputs

A refit reads a **training extract** (`tasktor.extract/1`): numbers only (hashed token buckets with dwell weights, dense
columns, day numbers, app indices) plus the frequent-term dictionary used for cluster names. It never sees a database or a
title. Every threshold is in `worker/prereg.json`, whose hash is stamped into each model; `eval.json` next to each model
lists every gate with its value, its threshold and the group-by-app null model's value.
