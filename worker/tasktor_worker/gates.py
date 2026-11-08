"""Label-free evaluation (C6): DBCV, bootstrap stability, temporal ARI, the beyond-app gates."""
import numpy as np
from scipy.sparse.csgraph import minimum_spanning_tree
from scipy.spatial.distance import cdist
from scipy.special import logsumexp
from sklearn.metrics import adjusted_mutual_info_score, adjusted_rand_score


def dbcv(x, labels, sample=3000, seed=7):
    """Density-Based Clustering Validation (Moulavi et al. 2014) on a sample; noise counts in the denominator."""
    rng = np.random.default_rng(seed)
    idx = np.arange(len(x))
    if len(idx) > sample:
        idx = np.sort(rng.choice(idx, sample, replace=False))
    x, labels = x[idx], labels[idx]
    clusters = [c for c in np.unique(labels) if c >= 0]
    if len(clusters) < 2:
        return 0.0
    dim = x.shape[1]
    core, internal, dsc = {}, {}, {}
    for c in clusters:
        pts = x[labels == c]
        if len(pts) < 2:
            continue
        d = cdist(pts, pts)
        np.fill_diagonal(d, np.inf)
        d = np.maximum(d, 1e-12)
        logs = np.where(np.isinf(d), -np.inf, -dim * np.log(d))
        lse = logsumexp(logs, axis=1) - np.log(len(pts) - 1)
        cd = np.exp(-lse / dim)
        np.fill_diagonal(d, 0.0)
        mr = np.maximum(np.maximum(cd[:, None], cd[None, :]), d)
        mst = minimum_spanning_tree(mr).toarray()
        mst = np.maximum(mst, mst.T)
        degree = (mst > 0).sum(axis=1)
        inner = np.where(degree > 1)[0]
        if len(inner) == 0:
            inner = np.arange(len(pts))
        sub = mst[np.ix_(inner, inner)]
        dsc[c] = float(sub.max()) if (sub > 0).any() else float(mst.max())
        core[c] = cd[inner]
        internal[c] = pts[inner]
    valid = [c for c in clusters if c in dsc]
    if len(valid) < 2:
        return 0.0
    total = 0.0
    for c in valid:
        sep = np.inf
        for o in valid:
            if o == c:
                continue
            d = cdist(internal[c], internal[o])
            mr = np.maximum(np.maximum(core[c][:, None], core[o][None, :]), d)
            sep = min(sep, float(mr.min()))
        v = (sep - dsc[c]) / max(sep, dsc[c]) if max(sep, dsc[c]) > 0 else 0.0
        total += (labels == c).sum() / len(labels) * v
    return float(total)


def jaccard(a, b):
    a, b = set(a), set(b)
    return len(a & b) / len(a | b) if a | b else 0.0


def bootstrap_stability(x, labels, fit, resamples=20, fraction=0.8, seed=7):
    """Mean best-match Jaccard per cluster over resamples (Hennig): {cluster: stability}."""
    rng = np.random.default_rng(seed)
    clusters = [c for c in np.unique(labels) if c >= 0]
    scores = {c: [] for c in clusters}
    n = len(x)
    for _ in range(resamples):
        idx = np.sort(rng.choice(n, int(n * fraction), replace=False))
        lab = fit(x[idx])
        inside = set(idx.tolist())
        for c in clusters:
            members = [i for i in np.where(labels == c)[0] if i in inside]
            if not members:
                scores[c].append(0.0)
                continue
            best = 0.0
            for r in np.unique(lab):
                if r < 0:
                    continue
                best = max(best, jaccard(members, idx[lab == r]))
            scores[c].append(best)
    return {c: float(np.mean(v)) for c, v in scores.items()}


def ari(a, b):
    return float(adjusted_rand_score(a, b))


def ami(a, b):
    return float(adjusted_mutual_info_score(a, b))


def beyond_app(labels, apps):
    """G6: shown clusters that join several apps or split one app."""
    count = 0
    clusters = [c for c in np.unique(labels) if c >= 0]
    for c in clusters:
        mine = apps[labels == c]
        values, counts = np.unique(mine, return_counts=True)
        joins = (counts / counts.sum() >= 0.1).sum() >= 2
        main = values[np.argmax(counts)]
        splits = len({l for l in labels[(apps == main) & (labels >= 0)]}) >= 2
        if joins or splits:
            count += 1
    return count


def cos_gain(text, labels, apps, train, test):
    """G6b: mean cos(text, cluster centroid) − mean cos(text, app centroid) on held-out points."""
    def unit(v):
        n = np.linalg.norm(v, axis=-1, keepdims=True)
        return np.divide(v, n, out=np.zeros_like(v), where=n > 0)

    t = unit(text)
    pts = [i for i in test if labels[i] >= 0]
    if not pts:
        return 0.0
    cl_c, app_c = {}, {}
    for c in {labels[i] for i in train if labels[i] >= 0}:
        cl_c[c] = unit(t[[i for i in train if labels[i] == c]].mean(axis=0))
    for a in {apps[i] for i in train}:
        app_c[a] = unit(t[[i for i in train if apps[i] == a]].mean(axis=0))
    gains = []
    for i in pts:
        if labels[i] in cl_c and apps[i] in app_c:
            gains.append(float(t[i] @ cl_c[labels[i]] - t[i] @ app_c[apps[i]]))
    return float(np.mean(gains)) if gains else 0.0
