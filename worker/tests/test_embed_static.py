"""Static embeddings (1 MB budget): safetensors reading, pruning to the budget, the encoder, and the C# parity fixture."""
import json
import pathlib
import struct

import numpy as np

from tasktor_worker.embed import g8
from tasktor_worker.embed_static import StaticEncoder, convert, keep_order, read_safetensors

FIXTURE = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "embedding-static"
CONCEPTS = {
    "car": ["car", "automobile", "vehicle", "sedan"],
    "dog": ["dog", "puppy", "hound", "canine"],
    "tax": ["tax", "levy", "tariff", "excise"],
}
TEXTS = ["car", "renew the automobile insurance", "walk the puppy", "", "excise and levy rules", "Zebra!", "café"]


def fake_model(folder, dims=8):
    """A Model2Vec-shaped folder: a BERT-like vocabulary and an F32 `embeddings` tensor where each concept's words share a direction."""
    chars = list("abcdefghijklmnopqrstuvwxyz!.,") + ["##" + c for c in "abcdefghijklmnopqrstuvwxyz"]
    words = [w for ws in CONCEPTS.values() for w in ws] + ["the", "and", "rules", "insurance", "walk", "renew", "cafe"]
    tokens = ["[PAD]", "[unused0]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"] + chars + words
    vocab = {t: i for i, t in enumerate(tokens)}
    rng = np.random.default_rng(5)
    table = rng.normal(scale=0.1, size=(len(tokens), dims)).astype(np.float32)
    base = {c: rng.normal(size=dims) for c in CONCEPTS}
    for c, ws in CONCEPTS.items():
        for w in ws:
            table[vocab[w]] = base[c] + rng.normal(scale=0.05, size=dims)
    folder.mkdir(parents=True, exist_ok=True)
    data = table.astype("<f4").tobytes()
    header = json.dumps({"embeddings": {"dtype": "F32", "shape": list(table.shape), "data_offsets": [0, len(data)]}}).encode()
    (folder / "model.safetensors").write_bytes(struct.pack("<Q", len(header)) + header + data)
    (folder / "tokenizer.json").write_text(json.dumps({"model": {"type": "WordPiece", "vocab": vocab}}), encoding="utf-8")
    return table, vocab


def test_safetensors_are_read_without_the_package(tmp_path):
    table, _ = fake_model(tmp_path / "m")
    assert np.array_equal(read_safetensors(tmp_path / "m" / "model.safetensors")["embeddings"], table)


def test_pruning_keeps_specials_and_characters_first_and_fits_the_budget(tmp_path):
    _, vocab = fake_model(tmp_path / "m")
    order = keep_order(vocab)
    assert order[:5] == ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"]
    assert "[unused0]" not in order
    assert order.index("a") < order.index("car") and order.index("##z") < order.index("car")
    report = convert(tmp_path / "m", tmp_path / "out", budget=900)
    on_disk = (tmp_path / "out" / "static.i8").stat().st_size + (tmp_path / "out" / "vocab.txt").stat().st_size
    assert on_disk <= 900 and report["bytes"] == on_disk and report["rows"] < report["of"]


def test_the_encoder_averages_tokens_and_passes_g8_on_paraphrases(tmp_path):
    fake_model(tmp_path / "m")
    convert(tmp_path / "m", tmp_path / "out", budget=1_000_000)
    enc = StaticEncoder.load(tmp_path / "out")
    v = enc.encode(["car", "", "the zzz"])
    assert np.isclose(np.linalg.norm(v[0]), 1) and not v[1].any()
    texts = [w for ws in CONCEPTS.values() for w in ws]
    labels = [c for c, ws in CONCEPTS.items() for _ in ws]
    assert g8(texts, labels, enc, gain=0.05)["pass"] is True


def test_the_csharp_parity_fixture_is_current(tmp_path):
    """tests/fixtures/embedding-static holds a converted model and the vectors the C# StaticEmbedder must reproduce."""
    fake_model(tmp_path / "m")
    convert(tmp_path / "m", tmp_path / "out", budget=1_000_000)
    assert (tmp_path / "out" / "static.i8").read_bytes() == (FIXTURE / "static.i8").read_bytes()
    assert (tmp_path / "out" / "vocab.txt").read_text(encoding="utf-8") == (FIXTURE / "vocab.txt").read_text(encoding="utf-8")
    expected = json.loads((FIXTURE / "expected.json").read_text(encoding="utf-8"))
    got = StaticEncoder.load(FIXTURE).encode(expected["texts"])
    assert np.allclose(got, np.array(expected["vectors"], dtype=np.float32), atol=1e-6)


def test_the_smoke_check_set_is_well_formed():
    s = json.loads((pathlib.Path(__file__).resolve().parents[1] / "g8-smoke.json").read_text(encoding="utf-8"))
    assert len(s["texts"]) == len(s["labels"]) == 30
    assert len(set(s["labels"])) == 5
