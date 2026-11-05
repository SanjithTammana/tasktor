"""B4.7 helper: atomic writes in the tasktor.job/1 shape, debounce, OSC 9;4, polite pause."""
import io
import json
import pathlib
import sys

import jsonschema

from tasktor_progress import Progress

SCHEMA = json.loads((pathlib.Path(__file__).resolve().parents[2] / "schemas" / "tasktor.job-1.json").read_text())


class Clock:
    def __init__(self):
        self.t = 0.0

    def __call__(self):
        return self.t


def test_writes_a_valid_file_atomically(tmp_path):
    p = Progress("embed-run", total=10, unit="docs", heartbeat_s=30, folder=tmp_path, osc=False)
    p.update(4, msg="batch 4")
    doc = json.loads(p.path.read_text())
    jsonschema.Draft202012Validator(SCHEMA).validate(doc)
    assert doc["id"] == "embed-run" and doc["total"] == 10 and doc["heartbeat_s"] == 30
    assert not list(tmp_path.glob("*.tmp"))


def test_faster_writes_are_debounced(tmp_path):
    clock = Clock()
    p = Progress("fast", total=1000, folder=tmp_path, osc=False, clock=clock)
    for i in range(100):
        clock.t += 0.01
        p.update(i)
    assert p.writes <= 2
    clock.t += 1.5
    p.update(100)
    assert json.loads(p.path.read_text())["done"] == 100


def test_done_and_fail_set_the_state(tmp_path):
    p = Progress("job", folder=tmp_path, osc=False, min_interval=0)
    p.update(pct=42.5)
    assert json.loads(p.path.read_text())["pct"] == 42.5
    p.fail("oops")
    assert json.loads(p.path.read_text())["state"] == "error"


def test_osc_9_4_matches_the_file(tmp_path, monkeypatch):
    out = io.StringIO()
    monkeypatch.setattr(sys, "stdout", out)
    p = Progress("osc", total=4, folder=tmp_path, osc=True, min_interval=0)
    p.update(1)
    assert "\x1b]9;4;1;25\x07" in out.getvalue()
    p.done()
    assert out.getvalue().endswith("\x1b]9;4;0;100\x07")


def test_polite_pause_blocks_in_update_and_marks_paused(tmp_path):
    p = Progress("pp", total=10, folder=tmp_path, osc=False, min_interval=0)
    p.control_path.write_text('{"pause": true}')
    seen = []

    def fake_sleep(_):
        seen.append(json.loads(p.path.read_text()).get("paused"))
        p.control_path.write_text('{"pause": false}')

    p._sleep = fake_sleep
    p.update(3)
    assert seen == [True]
    assert "paused" not in json.loads(p.path.read_text())


def test_bad_ids_are_refused(tmp_path):
    import pytest
    with pytest.raises(ValueError):
        Progress("../x", folder=tmp_path)
