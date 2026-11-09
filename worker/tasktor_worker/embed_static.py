"""C21 static embeddings within a 1 MB model budget (owner decision 2026-11-09).

A Model2Vec-style distilled model (e.g. minishlab/potion-base-2M: `model.safetensors` + `tokenizer.json`, downloaded by
the owner) is pruned to the tokens that fit the budget and stored int8 with one scale per row:

    static.i8   b"TSE1", uint32 rows, uint32 dims, float32 scale[rows], int8 table[rows * dims]   (little-endian)
    vocab.txt   one token per row, in table order

A text's vector = the mean of its WordPiece tokens' rows (special tokens skipped), L2-normalised. The capture host
(C#, `StaticEmbedder`) does the same in process: no Python, no ONNX, about 1 MB of table.
"""
import json
import pathlib
import struct

import numpy as np

from tasktor_worker.embed import WordPiece

MAGIC = b"TSE1"
SPECIALS = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"]


def read_safetensors(path):
    """The tensors of a .safetensors file (F32 / F16 / BF16), without the safetensors package."""
    raw = pathlib.Path(path).read_bytes()
    n = struct.unpack("<Q", raw[:8])[0]
    header = json.loads(raw[8 : 8 + n])
    base = 8 + n
    out = {}
    for name, meta in header.items():
        if name == "__metadata__":
            continue
        a, b = meta["data_offsets"]
        buf = raw[base + a : base + b]
        dtype = meta["dtype"]
        if dtype == "F32":
            arr = np.frombuffer(buf, dtype="<f4")
        elif dtype == "F16":
            arr = np.frombuffer(buf, dtype="<f2").astype(np.float32)
        elif dtype == "BF16":
            arr = (np.frombuffer(buf, dtype="<u2").astype(np.uint32) << 16).view(np.float32)
        else:
            raise ValueError("unsupported dtype " + dtype)
        out[name] = arr.reshape(meta["shape"])
    return out


def keep_order(vocab):
    """Which tokens to keep first: specials, single characters and '##' + one character (so any word still splits),
    then whole words and longer pieces in vocabulary order (BERT's order is roughly by frequency)."""
    tokens = sorted(vocab, key=lambda t: vocab[t])
    usable = [t for t in tokens if not (t.startswith("[unused") or (t.startswith("[") and t.endswith("]") and t not in SPECIALS))]
    first = [t for t in SPECIALS if t in vocab]
    chars = [t for t in usable if t not in first and (len(t) == 1 or (t.startswith("##") and len(t) == 3))]
    rest = [t for t in usable if t not in first and t not in chars]
    return first + chars + rest


def rows_for(budget, dims, tokens):
    """The most rows whose table, scales and vocabulary fit the budget."""
    used, rows = 12, 0
    for t in tokens:
        cost = dims + 4 + len(t.encode("utf-8")) + 1
        if used + cost > budget:
            break
        used += cost
        rows += 1
    return rows


def quantize_rows(m):
    scale = np.maximum(np.abs(m).max(axis=1), 1e-12) / 127.0
    q = np.clip(np.rint(m / scale[:, None]), -127, 127).astype(np.int8)
    return q, scale.astype(np.float32)


def convert(model_dir, out_dir, budget=1_000_000):
    """Prune and quantise a Model2Vec model to `budget` bytes on disk (and in memory). Returns a report (numbers only)."""
    model_dir, out_dir = pathlib.Path(model_dir), pathlib.Path(out_dir)
    tensors = read_safetensors(model_dir / "model.safetensors")
    table = tensors.get("embeddings")
    if table is None:
        table = next(iter(tensors.values()))
    tok = json.loads((model_dir / "tokenizer.json").read_text(encoding="utf-8"))
    vocab = tok["model"]["vocab"]
    order = [t for t in keep_order(vocab) if vocab[t] < table.shape[0]]
    rows = rows_for(budget, table.shape[1], order)
    kept = order[:rows]
    q, scale = quantize_rows(np.asarray(table[[vocab[t] for t in kept]], dtype=np.float32))
    out_dir.mkdir(parents=True, exist_ok=True)
    blob = MAGIC + struct.pack("<II", rows, table.shape[1]) + scale.astype("<f4").tobytes() + q.tobytes()
    (out_dir / "static.i8").write_bytes(blob)
    # newline="\n": on Windows text mode would write \r\n, overflowing the budget and leaving \r on every token.
    (out_dir / "vocab.txt").write_text("\n".join(kept) + "\n", encoding="utf-8", newline="\n")
    size = len(blob) + (out_dir / "vocab.txt").stat().st_size
    return {"rows": rows, "of": len(order), "dims": int(table.shape[1]), "bytes": size, "budget": budget}


class StaticEncoder:
    """Mean of token rows, L2-normalised (the C# StaticEmbedder computes the same numbers)."""

    def __init__(self, vocab, scale, table):
        self.tokenizer = WordPiece(vocab)
        self.skip = {self.tokenizer.vocab[t] for t in SPECIALS if t in self.tokenizer.vocab}
        self.rows = table.astype(np.float32) * scale[:, None]

    @classmethod
    def load(cls, folder):
        folder = pathlib.Path(folder)
        raw = (folder / "static.i8").read_bytes()
        if raw[:4] != MAGIC:
            raise ValueError("not a static embedding table")
        rows, dims = struct.unpack("<II", raw[4:12])
        scale = np.frombuffer(raw[12 : 12 + 4 * rows], dtype="<f4")
        table = np.frombuffer(raw[12 + 4 * rows : 12 + 4 * rows + rows * dims], dtype=np.int8).reshape(rows, dims)
        vocab = (folder / "vocab.txt").read_text(encoding="utf-8").split("\n")[:rows]
        return cls(vocab, scale, table)

    def encode(self, texts):
        out = np.zeros((len(texts), self.rows.shape[1]), dtype=np.float32)
        for i, text in enumerate(texts):
            ids = [t for t in self.tokenizer.encode(text, max_tokens=10**6)[1:-1] if t not in self.skip]
            if ids:
                v = self.rows[ids].mean(axis=0)
                n = np.linalg.norm(v)
                out[i] = v / n if n > 0 else v
        return out
