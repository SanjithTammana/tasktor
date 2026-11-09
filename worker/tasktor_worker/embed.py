"""C21 embeddings (D-C20, §9): chunk and query vectors for the content index, and gate G8.

The encoder is a local MiniLM-class ONNX model (OQ-C21) chosen by the owner and placed in a folder with `model.onnx`
and `vocab.txt`; nothing is downloaded. Vectors are mean-pooled, L2-normalised and int8-quantised (one scale per
vector). Gate G8 compares the encoder with hashed TF-IDF on a hand-labelled check set: KMeans with k = the number of
labels, ARI against the labels; the embedding ships only when it wins by the prereg margin.
"""
import base64
import json
import pathlib
import unicodedata

import numpy as np
from sklearn.cluster import KMeans
from sklearn.feature_extraction.text import HashingVectorizer, TfidfTransformer
from sklearn.metrics import adjusted_rand_score
from sklearn.preprocessing import normalize

MAX_TOKENS = 256
RRF_K = 60

# Accent folding for Latin letters (U+00C0–U+024F), generated once from NFD and fixed here so the C# encoder (which runs
# without ICU) folds exactly the same characters. Combining marks are dropped as well.
FOLD_FROM = "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöùúûüýÿĀāĂăĄąĆćĈĉĊċČčĎďĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĨĩĪīĬĭĮįİĴĵĶķĹĺĻļĽľŃńŅņŇňŌōŎŏŐőŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽžƠơƯưǍǎǏǐǑǒǓǔǕǖǗǘǙǚǛǜǞǟǠǡǦǧǨǩǪǫǬǭǰǴǵǸǹǺǻȀȁȂȃȄȅȆȇȈȉȊȋȌȍȎȏȐȑȒȓȔȕȖȗȘșȚțȞȟȦȧȨȩȪȫȬȭȮȯȰȱȲȳ"
FOLD_TO = "AAAAAACEEEEIIIINOOOOOUUUUYaaaaaaceeeeiiiinooooouuuuyyAaAaAaCcCcCcCcDdEeEeEeEeEeGgGgGgGgHhIiIiIiIiIJjKkLlLlLlNnNnNnOoOoOoRrRrRrSsSsSsSsTtTtUuUuUuUuUuUuWwYyYZzZzZzOoUuAaIiOoUuUuUuUuUuAaAaGgKkOoOojGgNnAaAaAaEeEeIiIiOoOoRrRrUuUuSsTtHhAaEeOoOoOoOoYy"
FOLD = dict(zip(FOLD_FROM, FOLD_TO))


class WordPiece:
    """BERT uncased tokenizer: lower-case, strip accents, split on whitespace and punctuation, greedy word pieces."""

    def __init__(self, vocab):
        self.vocab = {t: i for i, t in enumerate(vocab)}
        self.unk = self.vocab.get("[UNK]", 0)
        self.cls = self.vocab.get("[CLS]", 0)
        self.sep = self.vocab.get("[SEP]", 0)

    @classmethod
    def load(cls, path):
        return cls(pathlib.Path(path).read_text(encoding="utf-8").splitlines())

    @staticmethod
    def basic(text):
        text = "".join(FOLD.get(c, c) for c in text.lower() if unicodedata.category(c) != "Mn")
        words, cur = [], []
        for c in text:
            if c.isspace():
                if cur:
                    words.append("".join(cur))
                    cur = []
            elif unicodedata.category(c).startswith("P") or (c.isascii() and not c.isalnum()):
                if cur:
                    words.append("".join(cur))
                    cur = []
                words.append(c)
            else:
                cur.append(c)
        if cur:
            words.append("".join(cur))
        return words

    def pieces(self, word):
        if len(word) > 100:
            return [self.unk]
        out, start = [], 0
        while start < len(word):
            end, found = len(word), None
            while start < end:
                piece = ("##" if start > 0 else "") + word[start:end]
                if piece in self.vocab:
                    found = self.vocab[piece]
                    break
                end -= 1
            if found is None:
                return [self.unk]
            out.append(found)
            start = end
        return out

    def encode(self, text, max_tokens=MAX_TOKENS):
        ids = [self.cls]
        for w in self.basic(text):
            ids.extend(self.pieces(w))
            if len(ids) >= max_tokens - 1:
                break
        return ids[: max_tokens - 1] + [self.sep]


class OnnxEncoder:
    """Mean-pooled sentence vectors from a BERT-style ONNX model. `session` may be injected (tests)."""

    def __init__(self, tokenizer, session):
        self.tokenizer = tokenizer
        self.session = session
        self.inputs = {i.name for i in session.get_inputs()}

    @classmethod
    def load(cls, folder):
        import onnxruntime  # optional: only where a model is installed

        folder = pathlib.Path(folder)
        options = onnxruntime.SessionOptions()
        options.intra_op_num_threads = 1  # BelowNormal batch work: one core
        session = onnxruntime.InferenceSession(str(folder / "model.onnx"), options, providers=["CPUExecutionProvider"])
        return cls(WordPiece.load(folder / "vocab.txt"), session)

    def encode(self, texts, batch=16):
        out = []
        for i in range(0, len(texts), batch):
            ids = [self.tokenizer.encode(t) for t in texts[i : i + batch]]
            width = max(len(x) for x in ids)
            input_ids = np.zeros((len(ids), width), dtype=np.int64)
            mask = np.zeros((len(ids), width), dtype=np.int64)
            for r, x in enumerate(ids):
                input_ids[r, : len(x)] = x
                mask[r, : len(x)] = 1
            feed = {"input_ids": input_ids, "attention_mask": mask}
            if "token_type_ids" in self.inputs:
                feed["token_type_ids"] = np.zeros_like(input_ids)
            hidden = self.session.run(None, {k: v for k, v in feed.items() if k in self.inputs})[0]
            m = mask[:, :, None].astype(np.float32)
            pooled = (hidden * m).sum(axis=1) / np.maximum(m.sum(axis=1), 1e-9)
            out.append(normalize(pooled))
        return np.vstack(out) if out else np.zeros((0, 0), dtype=np.float32)


class HashedTfidf:
    """D-C4's cheap path as G8's comparison: hashed terms, TF-IDF, L2-normalised."""

    def __init__(self, buckets=2**18):
        self.hasher = HashingVectorizer(n_features=buckets, alternate_sign=False, norm=None)

    def encode(self, texts):
        return normalize(TfidfTransformer().fit_transform(self.hasher.transform(texts)))


def quantize(v):
    """int8 with one scale per vector (max |x| → 127)."""
    v = np.asarray(v, dtype=np.float32)
    scale = np.maximum(np.abs(v).max(axis=-1), 1e-12) / 127.0
    q = np.clip(np.rint(v / scale[..., None]), -127, 127).astype(np.int8)
    return q, scale.astype(np.float32)


def dequantize(q, scale):
    return q.astype(np.float32) * np.asarray(scale, dtype=np.float32)[..., None]


def rrf(lists, k=RRF_K):
    """Reciprocal-rank fusion: ids by Σ 1 / (k + rank), rank from 1; ties by first appearance."""
    score, first = {}, {}
    for lst in lists:
        for rank, i in enumerate(lst, start=1):
            score[i] = score.get(i, 0.0) + 1.0 / (k + rank)
            first.setdefault(i, len(first))
    return sorted(score, key=lambda i: (-score[i], first[i]))


def g8(texts, labels, encoder, gain, seed=7):
    """Gate G8 on the labelled check set: pass when ARI(embedding) − ARI(hashed TF-IDF) ≥ gain."""
    labels = np.asarray(labels)
    k = len(set(labels.tolist()))
    if k < 2 or len(texts) < k:
        return {"gate": "G8", "pass": False, "reason": "check set too small"}

    def ari(x):
        pred = KMeans(n_clusters=k, n_init=10, random_state=seed).fit_predict(x)
        return float(adjusted_rand_score(labels, pred))

    tfidf = ari(HashedTfidf().encode(texts))
    emb = ari(encoder.encode(texts))
    return {"gate": "G8", "embedding_ari": emb, "tfidf_ari": tfidf, "gain": emb - tfidf, "threshold": gain, "pass": emb - tfidf >= gain}


def embed_lines(encoder, lines, out):
    """JSON lines {"id", "text"} in → {"id", "q" (base64 int8), "s" (scale)} out. Texts are never echoed."""
    items = [json.loads(line) for line in lines if line.strip()]
    if not items:
        return 0
    q, s = quantize(encoder.encode([str(i["text"]) for i in items]))
    for item, row, scale in zip(items, q, s):
        out.write(json.dumps({"id": item["id"], "q": base64.b64encode(row.tobytes()).decode("ascii"), "s": float(scale)}) + "\n")
    out.flush()
    return len(items)
