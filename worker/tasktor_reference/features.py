"""Reference feature transform and exemplar assigner: mirrors Tasktor.Core.Workflow.ExemplarAssigner (D-C10).

An activity is (bucket ids, weights in ms, dense columns, app, prev app, next app). Blocks: text (log1p TF × IDF, L2,
× LSA, L2), app one-hot (+ other), time (hour sin / cos, weekend, log duration), context (prev / next one-hot), shape
(idle ratio, glances, title rate, job share); each block L2-normalised and weighted; then − mean, × PCA.
"""
import hashlib
import json
import math
import pathlib

import numpy as np

DENSE = ["hour", "weekend", "duration_min", "idle_ratio", "glances", "title_rate", "job_frac"]


def l2(v):
    n = float(np.linalg.norm(v))
    return v / n if n > 0 else v


def time_block(d):
    h = d[0]
    return np.array([math.sin(2 * math.pi * h / 24), math.cos(2 * math.pi * h / 24), d[1], math.log1p(max(d[2], 0.0)) / math.log1p(60.0)])


def shape_block(d):
    return np.array([d[3], min(d[4], 20.0) / 20.0, min(d[5], 5.0) / 5.0, d[6]])


def onehot(index, size):
    v = np.zeros(size)
    if index is not None and 0 <= index < size:
        v[index] = 1.0
    return v


class Model:
    """A model directory, read the way the agent maps it."""

    def __init__(self, path):
        self.path = pathlib.Path(path)
        self.manifest = json.loads((self.path / "model.json").read_text())
        m = self.manifest
        files = {f["name"]: f for f in m["files"]}
        self.buckets = self._arr("buckets.i32", np.int32, files)
        self.idf = self._arr("idf.f32", np.float32, files).astype(np.float64)
        self.svd = self._arr("svd.f32", np.float32, files).astype(np.float64).reshape(len(self.buckets), -1)
        pca = self._arr("pca.f32", np.float32, files).astype(np.float64).reshape(m["dims"], m["pca_dims"] + 1)
        self.mean, self.comps = pca[:, 0], pca[:, 1:]
        self.exemplars = self._arr("exemplars.f32", np.float32, files).astype(np.float64).reshape(-1, m["pca_dims"])
        self.apps = {a: i for i, a in enumerate(m["apps"])}
        self.ctx = {a: i for i, a in enumerate(m["context_apps"])}
        self.w = m["weights"]
        self.wf_space = m.get("workflow_space", [])
        self.wf_exemplars = self._arr("wf_exemplars.f32", np.float32, files).astype(np.float64).reshape(-1, max(1, len(self.wf_space))) if "wf_exemplars.f32" in files else np.zeros((0, 1))

    def _arr(self, name, dtype, files):
        data = (self.path / name).read_bytes()
        if name in files and hashlib.sha256(data).hexdigest() != files[name]["sha256"]:
            raise ValueError("sidecar hash mismatch: " + name)
        return np.frombuffer(data, dtype=dtype)

    def vector(self, buckets, weights_ms, dense, app, prev, nxt):
        """The ~100-d weighted block vector before PCA."""
        text = np.zeros(self.svd.shape[1])
        idx = np.searchsorted(self.buckets, buckets)
        tf = np.zeros(len(self.buckets))
        for b, w, i in zip(buckets, weights_ms, idx):
            if i < len(self.buckets) and self.buckets[i] == b and w > 0:
                tf[i] = math.log1p(float(w) / 1000.0) * self.idf[i]
        tf = l2(tf)
        if tf.any():
            text = l2(tf @ self.svd)
        a = len(self.apps)
        c = len(self.ctx)
        app_v = onehot(self.apps.get(app, a), a + 1)
        prev_v = onehot(self.ctx.get(prev, c) if prev is not None else None, c + 1)
        next_v = onehot(self.ctx.get(nxt, c) if nxt is not None else None, c + 1)
        d = [float(x) for x in dense]
        return np.concatenate([
            text * self.w["text"],
            l2(app_v) * self.w["app"],
            l2(time_block(d)) * self.w["time"],
            l2(np.concatenate([prev_v, next_v])) * self.w["context"],
            l2(shape_block(d)) * self.w["shape"],
        ])

    def project(self, x):
        return (x - self.mean) @ self.comps

    def assign(self, y):
        """(cluster id or None, confidence): nearest exemplar per cluster, confidence = clamp(1 − d / r_c, 0, 1)."""
        best, conf = None, 0.0
        for cl in self.manifest["clusters"]:
            if cl["exemplar_count"] == 0 or cl["status"] in ("retired", "hidden"):
                continue
            ex = self.exemplars[cl["exemplar_start"]: cl["exemplar_start"] + cl["exemplar_count"]]
            d = float(np.min(np.linalg.norm(ex - y, axis=1)))
            if d > cl["radius"]:
                continue
            c = max(0.0, min(1.0, 1.0 - d / cl["radius"])) if cl["radius"] > 0 else 0.0
            if best is None or c > conf or (c == conf and cl["id"] < best):
                best, conf = cl["id"], c
        return best, conf

    def assign_window(self, share):
        """Workflow of a window from its dwell share over the workflow space."""
        best, conf = None, 0.0
        for wf in self.manifest.get("workflows", []):
            if wf["exemplar_count"] == 0 or wf["status"] in ("retired", "hidden"):
                continue
            ex = self.wf_exemplars[wf["exemplar_start"]: wf["exemplar_start"] + wf["exemplar_count"]]
            d = float(np.min(np.linalg.norm(ex - share, axis=1)))
            if d > wf["radius"]:
                continue
            c = max(0.0, min(1.0, 1.0 - d / wf["radius"])) if wf["radius"] > 0 else 0.0
            if best is None or c > conf or (c == conf and wf["id"] < best):
                best, conf = wf["id"], c
        return best, conf
