"""The refit: features → SVD → fits → bootstrap → eval → export (B5 stage names), from a numbers-only extract.

Every threshold comes from prereg.json (D-C6); its hash is stamped into the model. The model's own vectors are computed
with the reference transform from the files just written, so training and the agent's C# assigner see the same numbers.
"""
import datetime as dt
import hashlib
import json
import math
import os
import pathlib
import sys

import numpy as np
from scipy.optimize import linear_sum_assignment
from sklearn.cluster import HDBSCAN, KMeans
from sklearn.decomposition import TruncatedSVD
from sklearn.metrics import silhouette_score

from tasktor_reference.features import Model
from tasktor_worker import gates as G
from tasktor_worker.extract import Extract

STAGES = ["features", "SVD", "fits", "bootstrap", "eval", "export"]


def progress(stage, pct, msg="", out=None):
    out = out or sys.stdout
    out.write(json.dumps({"stage": stage, "pct": round(pct, 1), "msg": msg}) + "\n")
    out.flush()


def _write(path, arr, dtype):
    data = np.ascontiguousarray(arr, dtype=dtype).tobytes()
    pathlib.Path(path).write_bytes(data)
    return hashlib.sha256(data).hexdigest()


def hdbscan_labels(y, spec):
    n = len(y)
    mcs = max(spec["min_cluster_size"], int(math.ceil(spec["min_cluster_size_frac"] * n)))
    if n <= mcs:
        return np.full(n, -1), np.zeros(n)
    h = HDBSCAN(min_cluster_size=mcs, min_samples=min(spec["min_samples"], n - 1), cluster_selection_method="eom", copy=True)
    h.fit(y)
    return h.labels_, h.probabilities_


def exemplars_and_radius(y, labels, prob, cluster, per, pct):
    members = np.where(labels == cluster)[0]
    order = sorted(members, key=lambda i: (-prob[i], i))[:per]
    ex = y[order]
    dist = np.linalg.norm(y[members][:, None, :] - ex[None, :, :], axis=2)
    for row, i in enumerate(members):  # leave-one-out: an exemplar's own distance does not count
        if i in order:
            dist[row, order.index(i)] = np.inf
    d = np.min(dist, axis=1) if len(order) > 1 else np.zeros(len(members))
    d = d[np.isfinite(d)]
    r = float(np.percentile(d, pct)) if len(d) else 0.0
    return ex, max(r, 1e-6)


class Fit:
    """Exemplar sets and radii for labels in a vector space; assigns like the C# assigner."""

    def __init__(self, y, labels, prob, per, pct):
        self.clusters = [c for c in np.unique(labels) if c >= 0]
        self.ex, self.r = {}, {}
        for c in self.clusters:
            self.ex[c], self.r[c] = exemplars_and_radius(y, labels, prob, c, per, pct)

    def assign(self, v):
        best, conf = -1, 0.0
        for c in self.clusters:
            d = float(np.min(np.linalg.norm(self.ex[c] - v, axis=1)))
            if d > self.r[c]:
                continue
            k = max(0.0, min(1.0, 1.0 - d / self.r[c]))
            if best < 0 or k > conf:
                best, conf = c, k
        return best, conf


def run(job, out=None):
    prereg_bytes = pathlib.Path(job["prereg"]).read_bytes()
    prereg = json.loads(prereg_bytes)
    prereg_hash = hashlib.sha256(prereg_bytes).hexdigest()
    fs, cs, gs, asg = prereg["features"], prereg["clustering"], prereg["gates"], prereg["assignment"]
    ext = Extract(job["extract"])
    outdir = pathlib.Path(job["output"])
    outdir.mkdir(parents=True, exist_ok=True)
    overrides = json.loads(pathlib.Path(job["overrides"]).read_text()) if job.get("overrides") and os.path.exists(job["overrides"]) else {}
    n = ext.n
    seed = cs["seed"]

    # ---- features: vocabularies, TF-IDF over buckets with df ≥ min_df ----
    progress("features", 0, "%d activities" % n, out)
    df = {}
    for i in range(n):
        for b in set(ext.tokens(i)[0].tolist()):
            df[b] = df.get(b, 0) + 1
    kept = np.array(sorted(b for b, c in df.items() if c >= fs["min_df"]), dtype=np.int32)
    idf = np.array([math.log((1 + n) / (1 + df[b])) + 1 for b in kept])
    pos = {int(b): i for i, b in enumerate(kept)}
    tf = np.zeros((n, len(kept)))
    for i in range(n):
        for b, w in zip(*ext.tokens(i)):
            j = pos.get(int(b))
            if j is not None and w > 0:
                tf[i, j] = math.log1p(float(w) / 1000.0) * idf[j]
    norms = np.linalg.norm(tf, axis=1, keepdims=True)
    tf = np.divide(tf, norms, out=np.zeros_like(tf), where=norms > 0)
    counts = {}
    for i in range(n):
        counts[ext.app(i)] = counts.get(ext.app(i), 0) + 1
    apps = sorted(a for a, c in counts.items() if c >= fs["min_app_activities"])
    ctx_counts = {}
    for i in range(n):
        for col in (1, 2):
            a = ext.app(i, col)
            if a is not None:
                ctx_counts[a] = ctx_counts.get(a, 0) + 1
    context = sorted(sorted(ctx_counts, key=lambda a: (-ctx_counts[a], a))[: fs["context_apps"]])

    # ---- SVD (LSA) to 64 ----
    progress("SVD", 20, "%d kept buckets" % len(kept), out)
    lsa = fs["lsa_dims"]
    svd = np.zeros((len(kept), lsa))
    if len(kept) >= 2 and n >= 2:
        k = min(lsa, len(kept) - 1, n - 1)
        t = TruncatedSVD(n_components=k, random_state=seed, algorithm="arpack" if k < min(tf.shape) else "randomized")
        t.fit(tf)
        svd[:, :k] = t.components_.T
    weights = {"text": fs["text_weight"], "app": fs["app_weight"], "time": fs["time_weight"], "context": fs["context_weight"], "shape": fs["shape_weight"]}
    dims = lsa + (len(apps) + 1) + 4 + 2 * (len(context) + 1) + 4
    files = {
        "buckets.i32": _write(outdir / "buckets.i32", kept, np.int32),
        "idf.f32": _write(outdir / "idf.f32", idf, np.float32),
        "svd.f32": _write(outdir / "svd.f32", svd, np.float32),
    }
    # Pre-PCA vectors through the reference transform (identity PCA first), then PCA to 16.
    pca_dims = fs["pca_dims"]
    ident = np.zeros((dims, pca_dims + 1))
    files["pca.f32"] = _write(outdir / "pca.f32", ident, np.float32)
    files["exemplars.f32"] = _write(outdir / "exemplars.f32", np.zeros((0, pca_dims)), np.float32)
    manifest = {
        "schema": "tasktor.model/1", "version": job.get("version") or outdir.name, "prereg_hash": prereg_hash, "feature_spec": fs["version"],
        "extract_hash": ext.hash, "created_at": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "apps": apps, "context_apps": context, "weights": weights, "dims": dims, "pca_dims": pca_dims, "clusters": [],
        "workflows": [], "workflow_space": [], "files": [], "activated": False, "failed_gates": [],
    }
    _manifest(outdir, manifest, files)
    m = Model(outdir)
    x = np.array([m.vector(*row) for row in ext.rows()])
    mean = x.mean(axis=0) if n else np.zeros(dims)
    xc = x - mean
    _, _, vt = np.linalg.svd(xc, full_matrices=False) if n else (None, None, np.zeros((pca_dims, dims)))
    comps = np.zeros((dims, pca_dims))
    kk = min(pca_dims, vt.shape[0])
    comps[:, :kk] = vt[:kk].T
    for j in range(kk):  # deterministic sign: the largest loading positive
        if comps[np.argmax(np.abs(comps[:, j])), j] < 0:
            comps[:, j] = -comps[:, j]
    files["pca.f32"] = _write(outdir / "pca.f32", np.column_stack([mean, comps]), np.float32)
    _manifest(outdir, manifest, files)
    m = Model(outdir)
    y = np.array([m.project(v) for v in x])
    text = x[:, :lsa]

    # ---- fits: HDBSCAN (main), k-means (baseline), group by app (null) ----
    progress("fits", 40, "", out)
    labels, prob = hdbscan_labels(y, cs)
    app_arr = np.array([ext.app(i) for i in range(n)], dtype=object)
    app_codes = np.array([sorted(set(app_arr)).index(a) for a in app_arr]) if n else np.zeros(0, int)
    kmeans_k, kmeans_sil = None, None
    if n >= cs["k_min"] * 2:
        rng = np.random.default_rng(seed)
        sample = np.sort(rng.choice(n, min(n, cs["sample"]), replace=False))
        for k in range(cs["k_min"], min(cs["k_max"], len(sample) - 1) + 1):
            lab = KMeans(n_clusters=k, n_init=3, random_state=seed).fit_predict(y[sample])
            s = float(silhouette_score(y[sample], lab)) if len(set(lab)) > 1 else -1
            if kmeans_sil is None or s > kmeans_sil:
                kmeans_k, kmeans_sil = k, s

    # ---- bootstrap stability (G3) ----
    progress("bootstrap", 60, "", out)
    stability = G.bootstrap_stability(y, labels, lambda sub: hdbscan_labels(sub, cs)[0], gs["g3_resamples"], gs["g3_fraction"], seed) if (labels >= 0).any() else {}

    # ---- eval ----
    progress("eval", 75, "", out)
    dwell = ext.dense[:, 2].astype(np.float64)
    hours = float(dwell.sum() / 60.0)
    days = len(set(ext.days.tolist()))
    g0 = n >= gs["g0_activities"] and days >= gs["g0_days"] and hours >= gs["g0_hours"]
    coverage = float(dwell[labels >= 0].sum() / dwell.sum()) if dwell.sum() > 0 else 0.0
    sep = G.dbcv(y, labels, cs["sample"], seed) if (labels >= 0).any() else 0.0
    odd = [i for i in range(n) if ext.days[i] % 2 == 1]
    even = [i for i in range(n) if ext.days[i] % 2 == 0]
    temporal, gain = 0.0, 0.0
    if len(odd) > cs["min_cluster_size"] and even:
        lab_odd, prob_odd = hdbscan_labels(y[odd], cs)
        fit_odd = Fit(y[odd], lab_odd, prob_odd, asg["exemplars_per_cluster"], asg["radius_percentile"])
        pred = np.array([fit_odd.assign(y[i])[0] for i in even])
        temporal = G.ari(labels[even], pred)
        odd_labels = np.full(n, -1)
        odd_labels[odd] = lab_odd
        for j, i in enumerate(even):
            odd_labels[i] = pred[j]
        gain = G.cos_gain(text, odd_labels, app_arr, odd, even)
    null_gain = 0.0
    beyond = G.beyond_app(labels, app_arr) if n else 0
    amis = G.ami(labels, app_codes) if n else 0.0
    sens = None
    if n > 20 and vt.shape[0] > pca_dims:
        y32 = xc @ vt[: min(32, vt.shape[0])].T  # G7: the same fit at 32 dims (episode-threshold variants need raw events)
        sens = G.ari(labels, hdbscan_labels(y32, cs)[0])
    report = [
        gate("G0", n, gs["g0_activities"], g0),
        gate("G1", coverage, gs["g1_coverage"], coverage >= gs["g1_coverage"], null=1.0),
        gate("G2", sep, gs["g2_dbcv"], sep >= gs["g2_dbcv"]),
        gate("G3", float(np.mean(list(stability.values()))) if stability else 0.0, gs["g3_show"], True, report_only=True),
        gate("G4", temporal, gs["g4_ari"], temporal >= gs["g4_ari"]),
        gate("G5", None, gs["g5_persistence"], True),
        gate("G6", beyond, gs["g6_clusters"], beyond >= gs["g6_clusters"], null=0.0),
        gate("G6b", gain, gs["g6b_gain"], gain >= gs["g6b_gain"], null=null_gain),
        gate("G7", sens, None, True, report_only=True),
        gate("baseline_kmeans", kmeans_sil, None, True, report_only=True),
        gate("ami_app", amis, None, True, report_only=True),
    ]

    # ---- stable ids (D-C12) against the previous model ----
    prev = Model(job["previous_model"]) if job.get("previous_model") and os.path.exists(os.path.join(job["previous_model"], "model.json")) else None
    clusters = [c for c in np.unique(labels) if c >= 0]
    ids, persistence = stable_ids(clusters, labels, y, x, prev, gs["match_jaccard"], prefix="a")
    if prev is not None:
        report[5] = gate("G5", persistence, gs["g5_persistence"], persistence >= gs["g5_persistence"])

    # ---- names: <top app> · <top 2 terms>, terms only from the frequent-term dictionary ----
    names = cluster_names(ext, labels, clusters)
    fit = Fit(y, labels, prob, asg["exemplars_per_cluster"], asg["radius_percentile"])
    ex_rows, table = [], []
    hide = set(overrides.get("hide", []))
    merge = overrides.get("merge", {})
    for c in sorted(clusters, key=lambda c: ids[c]):
        cid = ids[c]
        status = "shown" if stability.get(c, 0.0) >= gs["g3_show"] else "unstable"
        if cid in hide:
            status = "hidden"
        start = len(ex_rows)
        ex_rows.extend(fit.ex[c].tolist())
        table.append({"id": cid, "name": overrides.get("names", {}).get(cid, names[c]), "status": status, "stability": round(stability.get(c, 0.0), 4),
                      "radius": fit.r[c], "exemplar_start": start, "exemplar_count": len(fit.ex[c]), "size": int((labels == c).sum()),
                      **({"merged_into": merge[cid]} if cid in merge else {})})

    # ---- workflows: windows × shown activity clusters, dwell share ----
    wf_table, wf_rows, space = [], [], [t["id"] for t in table if t["status"] == "shown"]
    wins = ext.windows
    win_days = len({w["day"] for w in wins})
    wf_ok = len(wins) >= gs["g0_windows"] and win_days >= gs["g0_window_days"] and len(space) >= 2
    report.append(gate("G0w", len(wins), gs["g0_windows"], wf_ok, report_only=True))
    if wf_ok:
        idx_of = {cid: j for j, cid in enumerate(space)}
        lab_id = {c: ids[c] for c in clusters}
        share = np.zeros((len(wins), len(space)))
        for r, w in enumerate(wins):
            total = float(sum(w["dwell_ms"]))
            for a, ms in zip(w["activities"], w["dwell_ms"]):
                cid = lab_id.get(labels[a])
                if cid in idx_of and total > 0:
                    share[r, idx_of[cid]] += ms / total
        wl, wp = hdbscan_labels(share, cs)
        wfit = Fit(share, wl, wp, asg["exemplars_per_cluster"], asg["radius_percentile"])
        for k, c in enumerate(sorted(c for c in np.unique(wl) if c >= 0)):
            top = np.argsort(-share[wl == c].mean(axis=0))[:2]
            parts = list(dict.fromkeys(table[[t["id"] for t in table].index(space[j])]["name"].split(" · ")[0] for j in top))
            terms = [t for j in top for t in table[[x["id"] for x in table].index(space[j])]["name"].split(" · ")[1:2]]
            name = " + ".join(parts) + (" · " + terms[0].split(" ")[0] if terms else "")
            start = len(wf_rows)
            wf_rows.extend(wfit.ex[c].tolist())
            wf_table.append({"id": "w%d" % (k + 1), "name": name, "status": "shown", "stability": 0.0, "radius": wfit.r[c],
                             "exemplar_start": start, "exemplar_count": len(wfit.ex[c]), "size": int((wl == c).sum())})

    activated = all(r["pass"] for r in report if r["id"] in ("G0", "G1", "G2", "G4", "G6", "G6b"))
    failed = [r["id"] for r in report if not r["pass"] and not r.get("report_only")]

    # ---- export ----
    progress("export", 90, "", out)
    files["exemplars.f32"] = _write(outdir / "exemplars.f32", np.array(ex_rows).reshape(-1, pca_dims) if ex_rows else np.zeros((0, pca_dims)), np.float32)
    files["wf_exemplars.f32"] = _write(outdir / "wf_exemplars.f32", np.array(wf_rows).reshape(-1, max(1, len(space))) if wf_rows else np.zeros((0, max(1, len(space)))), np.float32)
    manifest.update({"clusters": table, "workflows": wf_table, "workflow_space": space if wf_table else [], "activated": activated, "failed_gates": failed})
    _manifest(outdir, manifest, files)
    (outdir / "eval.json").write_text(json.dumps({"schema": "tasktor.eval/1", "version": manifest["version"], "activated": activated, "gates": report}, indent=1))
    progress("export", 100, "activated" if activated else "not activated: " + ", ".join(failed), out)
    return manifest


def gate(gid, value, threshold, ok, null=None, report_only=False):
    g = {"id": gid, "value": None if value is None else round(float(value), 6), "threshold": threshold, "pass": bool(ok), "report_only": report_only}
    if null is not None:
        g["null_value"] = null
    if g["threshold"] is None:
        del g["threshold"]
    if g["value"] is None:
        del g["value"]
    return g


def _manifest(outdir, manifest, files):
    shapes = {}
    for name in files:
        shapes[name] = [(outdir / name).stat().st_size // 4]
    manifest["files"] = [{"name": k, "dtype": "i32" if k.endswith(".i32") else "f32", "shape": shapes[k], "sha256": v} for k, v in sorted(files.items())]
    (outdir / "model.json").write_text(json.dumps(manifest, indent=1))


def stable_ids(clusters, labels, y, x, prev, threshold, prefix):
    """D-C12: Hungarian matching on member Jaccard against the previous model's assignments; ≥ 0.5 keeps the id."""
    ids = {}
    if prev is None:
        for k, c in enumerate(sorted(clusters, key=lambda c: (-(labels == c).sum(), c))):
            ids[c] = "%s%d" % (prefix, k + 1)
        return ids, None
    old = np.array([prev.assign(prev.project(v))[0] for v in x], dtype=object)
    old_ids = sorted({o for o in old if o is not None})
    used = [int(t["id"][1:]) for t in prev.manifest["clusters"] if t["id"][1:].isdigit()]
    nxt = max(used, default=0) + 1
    if clusters and old_ids:
        cost = np.zeros((len(clusters), len(old_ids)))
        for i, c in enumerate(clusters):
            for j, o in enumerate(old_ids):
                cost[i, j] = -G.jaccard(np.where(labels == c)[0], np.where(old == o)[0])
        rows, cols = linear_sum_assignment(cost)
        for i, j in zip(rows, cols):
            if -cost[i, j] >= threshold:
                ids[clusters[i]] = old_ids[j]
    for c in clusters:
        if c not in ids:
            ids[c] = "%s%d" % (prefix, nxt)
            nxt += 1
    matched = sum(1 for o in old_ids if o in ids.values())
    return ids, (matched / len(old_ids) if old_ids else 1.0)


def cluster_names(ext, labels, clusters):
    """c-TF-IDF over the frequent-term dictionary; deterministic."""
    tf = {c: {} for c in clusters}
    for i in range(ext.n):
        c = labels[i]
        if c < 0:
            continue
        for b, w in zip(*ext.tokens(i)):
            b = int(b)
            if b in ext.terms:
                tf[c][b] = tf[c].get(b, 0.0) + float(w)
    avg = np.mean([sum(v.values()) for v in tf.values()]) if tf else 0.0
    freq = {}
    for v in tf.values():
        for b, w in v.items():
            freq[b] = freq.get(b, 0.0) + w
    names = {}
    for c in clusters:
        apps = [ext.app(i) for i in range(ext.n) if labels[i] == c]
        top_app = max(sorted(set(apps)), key=apps.count)
        base = top_app[:-4] if top_app.lower().endswith(".exe") else top_app
        total = sum(tf[c].values()) or 1.0
        scored = sorted(((w / total) * math.log(1 + avg / freq[b]), ext.terms[b]) for b, w in tf[c].items())
        terms = [t for _, t in sorted(scored, key=lambda s: (-s[0], s[1]))
                 if not t.startswith("dom:")][:2]
        names[c] = base.capitalize() + (" · " + " ".join(terms) if terms else "")
    return names
