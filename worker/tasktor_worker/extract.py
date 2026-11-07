"""Read the agent's numbers-only training extract (meta.json + tokens.csr / dense.f32 / days.i32 / apps.i32)."""
import hashlib
import json
import pathlib

import numpy as np


class Extract:
    def __init__(self, path, verify=True):
        self.path = pathlib.Path(path)
        self.meta = json.loads((self.path / "meta.json").read_text())
        files = self.meta["files"]
        if verify:
            for name, sha in files.items():
                if hashlib.sha256((self.path / name).read_bytes()).hexdigest() != sha:
                    raise ValueError("extract file changed: " + name)
        raw = (self.path / "tokens.csr").read_bytes()
        n, nnz = np.frombuffer(raw[:8], dtype=np.int32)
        off = 8
        self.indptr = np.frombuffer(raw[off: off + 4 * (n + 1)], dtype=np.int32)
        off += 4 * (n + 1)
        self.bucket = np.frombuffer(raw[off: off + 4 * nnz], dtype=np.int32)
        off += 4 * nnz
        self.weight = np.frombuffer(raw[off: off + 4 * nnz], dtype=np.float32)
        self.n = int(n)
        self.dense = np.frombuffer((self.path / "dense.f32").read_bytes(), dtype=np.float32).reshape(self.n, len(self.meta["dense"]))
        self.days = np.frombuffer((self.path / "days.i32").read_bytes(), dtype=np.int32)
        self.app_ids = np.frombuffer((self.path / "apps.i32").read_bytes(), dtype=np.int32).reshape(self.n, 3)
        self.apps = self.meta["apps"]
        self.terms = {int(k): v for k, v in self.meta.get("terms", {}).items()}
        self.windows = self.meta.get("windows", [])
        self.hash = hashlib.sha256((self.path / "meta.json").read_bytes()).hexdigest()

    def tokens(self, i):
        a, b = self.indptr[i], self.indptr[i + 1]
        return self.bucket[a:b], self.weight[a:b]

    def app(self, i, col=0):
        k = int(self.app_ids[i, col])
        return None if k < 0 else self.apps[k]

    def rows(self):
        for i in range(self.n):
            b, w = self.tokens(i)
            yield b, w, self.dense[i], self.app(i, 0), self.app(i, 1), self.app(i, 2)
