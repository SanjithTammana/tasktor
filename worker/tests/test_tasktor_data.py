"""The Python loader against a sample export that C# writes byte for byte (DataExporterTests keeps it current)."""
import json
import pathlib
import shutil

import pandas as pd
import pytest

import tasktor_data

SAMPLE = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "export-sample"


def test_load_returns_one_frame_per_manifest_table():
    tables = tasktor_data.load(SAMPLE)
    assert sorted(tables) == ["jobs_runs", "selfuse"]
    runs = tables["jobs_runs"]
    assert list(runs.columns) == ["kind", "ended_at", "duration_s", "exit_code", "good"]
    assert len(runs) == 2
    assert runs["duration_s"].tolist() == [4200.5, 3900.0]
    assert str(runs["ended_at"].dt.tz) == "UTC"
    assert runs["ended_at"].iloc[0] == pd.Timestamp("2026-11-04T09:00:00Z")
    assert runs["good"].tolist() == [True, False]


def test_csv_fallback_and_quoting(tmp_path):
    folder = tmp_path / "export"
    shutil.copytree(SAMPLE, folder)
    for f in folder.glob("*.jsonl"):
        f.unlink()
    manifest = json.loads((folder / "manifest.json").read_text())
    for t in manifest["tables"]:
        t["files"] = [f for f in t["files"] if f.endswith(".csv")]
    (folder / "manifest.json").write_text(json.dumps(manifest))
    tables = tasktor_data.load(folder)
    assert tables["selfuse"]["feature"].tolist() == ["action.open", 'card, "quoted"']
    assert tables["selfuse"]["count"].tolist() == [3, 1]


def test_left_out_tables_are_reported():
    manifest = tasktor_data.load_manifest(SAMPLE)
    assert any(x.startswith("events_raw") for x in manifest["left_out"])


def test_not_an_export(tmp_path):
    (tmp_path / "manifest.json").write_text('{"schema": "something/1"}')
    with pytest.raises(ValueError):
        tasktor_data.load(tmp_path)


def test_row_count_mismatch_is_an_error(tmp_path):
    folder = tmp_path / "export"
    shutil.copytree(SAMPLE, folder)
    (folder / "jobs_runs.jsonl").write_text((folder / "jobs_runs.jsonl").read_text().splitlines()[0] + "\n")
    with pytest.raises(ValueError):
        tasktor_data.load(folder)
