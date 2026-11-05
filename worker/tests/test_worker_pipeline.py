"""C5–C8 on the synthetic extract the C# side writes: gates, activation, stable ids, the model contract, parity fixture."""
import json
import os
import pathlib
import shutil
import subprocess
import sys

import jsonschema
import numpy as np
import pytest

from tasktor_reference.features import Model
from tasktor_worker.extract import Extract
from tasktor_worker.pipeline import run

ROOT = pathlib.Path(__file__).resolve().parents[2]
EXTRACT = ROOT / "tests" / "fixtures" / "workflow" / "extract"
MODEL = ROOT / "tests" / "fixtures" / "workflow" / "model"
PREREG = ROOT / "worker" / "prereg.json"


def schema(name):
    return jsonschema.Draft202012Validator(json.loads((ROOT / "schemas" / name).read_text()))


@pytest.fixture(scope="module")
def fitted(tmp_path_factory):
    out = tmp_path_factory.mktemp("models") / "v1"
    import io
    log = io.StringIO()
    manifest = run({"extract": str(EXTRACT), "prereg": str(PREREG), "output": str(out), "version": "v1"}, out=log)
    return out, manifest, log.getvalue()


def test_the_refit_reports_stages_and_activates_on_the_synthetic_weeks(fitted):
    out, manifest, log = fitted
    stages = [json.loads(l)["stage"] for l in log.splitlines()]
    assert stages[:6] == ["features", "SVD", "fits", "bootstrap", "eval", "export"]
    report = json.loads((out / "eval.json").read_text())
    gates = {g["id"]: g for g in report["gates"]}
    for g in ("G0", "G1", "G2", "G4", "G6", "G6b"):
        assert gates[g]["pass"], (g, gates[g])
    assert gates["G6b"]["value"] > gates["G6b"]["null_value"]  # beats the group-by-app null model
    assert manifest["activated"] and report["activated"]
    shown = [c for c in manifest["clusters"] if c["status"] == "shown"]
    assert len(shown) >= 4
    assert any("·" in c["name"] for c in shown)
    schema("tasktor.model-1.json").validate(manifest)
    schema("tasktor.eval-1.json").validate(report)


def test_names_use_only_frequent_terms_and_the_extract_has_no_titles(fitted):
    _, manifest, _ = fitted
    terms = set(json.loads((EXTRACT / "meta.json").read_text())["terms"].values())
    for c in manifest["clusters"]:
        words = c["name"].split(" · ")[1].split() if " · " in c["name"] else []
        assert all(w in terms for w in words)


def test_a_second_refit_keeps_ids_and_reports_persistence(fitted, tmp_path):
    first, manifest, _ = fitted
    second = tmp_path / "v2"
    m2 = run({"extract": str(EXTRACT), "prereg": str(PREREG), "output": str(second), "previous_model": str(first), "version": "v2"}, out=open(os.devnull, "w"))
    g5 = next(g for g in json.loads((second / "eval.json").read_text())["gates"] if g["id"] == "G5")
    assert g5["value"] >= 0.8 and g5["pass"]
    assert {c["id"] for c in m2["clusters"]} == {c["id"] for c in manifest["clusters"]}


def test_overrides_rename_and_hide_after_the_fit(fitted, tmp_path):
    _, manifest, _ = fitted
    target = manifest["clusters"][0]["id"]
    ov = tmp_path / "overrides.json"
    ov.write_text(json.dumps({"names": {target: "My renamed work"}, "hide": [manifest["clusters"][1]["id"]]}))
    m = run({"extract": str(EXTRACT), "prereg": str(PREREG), "output": str(tmp_path / "v3"), "overrides": str(ov)}, out=open(os.devnull, "w"))
    byid = {c["id"]: c for c in m["clusters"]}
    assert byid[target]["name"] == "My renamed work"
    assert byid[manifest["clusters"][1]["id"]]["status"] == "hidden"


def test_the_module_entry_point_reads_a_job_on_stdin(tmp_path):
    job = {"extract": str(EXTRACT), "prereg": str(PREREG), "output": str(tmp_path / "v9")}
    r = subprocess.run([sys.executable, "-m", "tasktor_worker"], input=json.dumps(job), capture_output=True, text=True, cwd=ROOT / "worker", timeout=300)
    assert r.returncode == 0, r.stderr
    assert json.loads(r.stdout.splitlines()[-1])["stage"] == "export"
    assert (tmp_path / "v9" / "model.json").exists()


def test_a_changed_extract_is_refused(tmp_path):
    bad = tmp_path / "x"
    shutil.copytree(EXTRACT, bad)
    (bad / "dense.f32").write_bytes(b"\0" * 8)
    with pytest.raises(ValueError):
        Extract(bad)


def expected_assignments(model_dir):
    m = Model(model_dir)
    ext = Extract(EXTRACT)
    rows = []
    for i, row in enumerate(ext.rows()):
        y = m.project(m.vector(*row))
        cid, conf = m.assign(y)
        rows.append({"index": i, "cluster": cid, "confidence": round(conf, 9), "y": [round(float(v), 9) for v in y]})
    wins = []
    for w in ext.windows:
        total = float(sum(w["dwell_ms"]))
        share = np.zeros(len(m.wf_space))
        for a, ms in zip(w["activities"], w["dwell_ms"]):
            c = rows[a]["cluster"]
            if c in m.wf_space and total > 0:
                share[m.wf_space.index(c)] += ms / total
        wid, wc = m.assign_window(share)
        wins.append({"id": w["id"], "workflow": wid, "confidence": round(wc, 9)})
    return {"activities": rows, "windows": wins}


def test_the_committed_parity_fixture_is_reproduced_by_the_reference_assigner(fitted):
    if os.environ.get("TASKTOR_REGEN_FIXTURES") == "1":
        out, _, _ = fitted
        if MODEL.exists():
            shutil.rmtree(MODEL)
        shutil.copytree(out, MODEL)
        (MODEL / "expected.json").write_text(json.dumps(expected_assignments(MODEL), indent=1))
    expected = json.loads((MODEL / "expected.json").read_text())
    got = expected_assignments(MODEL)
    assert [r["cluster"] for r in got["activities"]] == [r["cluster"] for r in expected["activities"]]
    assert np.allclose([r["confidence"] for r in got["activities"]], [r["confidence"] for r in expected["activities"]], atol=1e-7)
    assert sum(r["cluster"] is not None for r in got["activities"]) > 0.6 * len(got["activities"])
