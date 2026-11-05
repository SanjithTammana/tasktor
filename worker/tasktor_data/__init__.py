"""Load a Tasktor data export (tasktor.export/1) into pandas DataFrames (U2 D-S7).

    import tasktor_data
    tables = tasktor_data.load("Documents/Tasktor exports/tasktor-export-20261104-120000")
    tables["jobs_runs"].describe()

It reads exports only, so it never locks the live stores. JSONL is preferred (it keeps types); CSV is the fallback.
Columns named ``*_at`` / ``since`` / ``at`` are parsed as UTC timestamps.
"""
from __future__ import annotations

import json
import pathlib
from typing import Dict

__all__ = ["load", "load_manifest", "SCHEMA"]

SCHEMA = "tasktor.export/1"


def load_manifest(folder) -> dict:
    """Read and check ``manifest.json``."""
    path = pathlib.Path(folder) / "manifest.json"
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest.get("schema") != SCHEMA:
        raise ValueError(f"not a Tasktor export (schema {manifest.get('schema')!r}, expected {SCHEMA!r})")
    return manifest


def _is_time(column: str) -> bool:
    return column == "at" or column == "since" or column.endswith("_at")


def load(folder) -> Dict[str, "pandas.DataFrame"]:
    """Return ``{table name: DataFrame}`` for every table listed in the export's manifest."""
    import pandas as pd

    folder = pathlib.Path(folder)
    manifest = load_manifest(folder)
    out = {}
    for table in manifest["tables"]:
        name = table["name"]
        columns = table["columns"]
        files = table["files"]
        jsonl = folder / f"{name}.jsonl"
        csv = folder / f"{name}.csv"
        if f"{name}.jsonl" in files and jsonl.exists():
            frame = pd.read_json(jsonl, lines=True, dtype=False) if jsonl.stat().st_size else pd.DataFrame(columns=columns)
        elif f"{name}.csv" in files and csv.exists():
            frame = pd.read_csv(csv, keep_default_na=False, na_values=[""])
        else:
            raise FileNotFoundError(f"{name}: none of {files} found in {folder}")
        frame = frame.reindex(columns=columns)
        for column in columns:
            if _is_time(column) and len(frame):
                frame[column] = pd.to_datetime(frame[column], utc=True)
        if len(frame) != table["rows"]:
            raise ValueError(f"{name}: manifest says {table['rows']} rows, file has {len(frame)}")
        out[name] = frame
    return out
