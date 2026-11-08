"""C21 embeddings: tokenizer, pooling, int8, RRF and gate G8 (with a fake ONNX session; no model is downloaded)."""
import base64
import io
import json

import numpy as np

from tasktor_worker.embed import HashedTfidf, OnnxEncoder, WordPiece, dequantize, embed_lines, g8, quantize, rrf

CONCEPTS = {
    "car": ["car", "automobile", "vehicle", "sedan", "coupe", "hatchback"],
    "dog": ["dog", "puppy", "hound", "canine", "terrier", "beagle"],
    "tax": ["tax", "levy", "duty", "tariff", "excise", "toll"],
}
VOCAB = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", ".", "un", "##know", "##n"] + [w for ws in CONCEPTS.values() for w in ws]


class Input:
    def __init__(self, name):
        self.name = name


class FakeSession:
    """A BERT stand-in: each word's hidden state is its concept's direction (special tokens are zero)."""

    def __init__(self):
        rng = np.random.default_rng(3)
        base = {c: rng.normal(size=8) for c in CONCEPTS}
        self.table = np.zeros((len(VOCAB), 8), dtype=np.float32)
        for c, words in CONCEPTS.items():
            for w in words:
                self.table[VOCAB.index(w)] = base[c] + rng.normal(scale=0.05, size=8)
        self.calls = []

    def get_inputs(self):
        return [Input("input_ids"), Input("attention_mask"), Input("token_type_ids")]

    def run(self, outputs, feed):
        self.calls.append(set(feed))
        return [self.table[feed["input_ids"]]]


def encoder():
    return OnnxEncoder(WordPiece(VOCAB), FakeSession())


def test_wordpiece_splits_lowercases_and_marks_unknowns():
    tok = WordPiece(VOCAB)
    assert tok.basic("Car, Dog.") == ["car", ",", "dog", "."]
    assert tok.encode("Unknown car") == [2, 5, 6, 7, VOCAB.index("car"), 3]
    assert tok.encode("zebra") == [2, 1, 3]
    assert len(tok.encode("car " * 1000)) == 256


def test_vectors_are_mean_pooled_unit_length_and_padding_is_ignored():
    e = encoder()
    v = e.encode(["car", "car car car car car"])
    assert np.allclose(np.linalg.norm(v, axis=1), 1)
    assert np.allclose(v[0], v[1], atol=1e-5)  # padding of the shorter row does not count
    assert {"input_ids", "attention_mask", "token_type_ids"} <= e.session.calls[0]


def test_int8_round_trip_keeps_cosines():
    rng = np.random.default_rng(1)
    v = rng.normal(size=(20, 384)).astype(np.float32)
    v /= np.linalg.norm(v, axis=1, keepdims=True)
    q, s = quantize(v)
    assert q.dtype == np.int8 and q.shape == (20, 384) and s.shape == (20,)
    d = dequantize(q, s)
    assert np.abs((d @ d.T) - (v @ v.T)).max() < 0.02


def test_rrf_matches_the_spec_and_breaks_ties_by_first_appearance():
    assert rrf([["a", "b", "c"], ["c", "a"]]) == ["a", "c", "b"]
    assert rrf([["x"], ["y"]]) == ["x", "y"]
    assert rrf([]) == []


def test_g8_passes_an_encoder_that_clusters_paraphrases_tfidf_cannot():
    texts, labels = [], []
    for c, words in CONCEPTS.items():
        for w in words:
            texts.append(w)
            labels.append(c)
    report = g8(texts, labels, encoder(), gain=0.05)
    assert report["pass"] is True
    assert report["embedding_ari"] > report["tfidf_ari"] + 0.05


def test_g8_fails_when_the_encoder_is_no_better_than_tfidf():
    texts = ["car car", "car", "dog dog", "dog", "tax tax", "tax"]
    labels = ["a", "a", "b", "b", "c", "c"]
    report = g8(texts, labels, HashedTfidf(), gain=0.05)
    assert report["pass"] is False
    assert g8(["car"], ["a"], encoder(), gain=0.05)["pass"] is False


def test_embed_lines_writes_vectors_and_never_the_text():
    out = io.StringIO()
    n = embed_lines(encoder(), ['{"id": 7, "text": "secret car"}', "", '{"id": 9, "text": "dog"}'], out)
    assert n == 2
    rows = [json.loads(line) for line in out.getvalue().splitlines()]
    assert [r["id"] for r in rows] == [7, 9]
    assert "secret" not in out.getvalue()
    assert len(base64.b64decode(rows[0]["q"])) == 8 and rows[0]["s"] > 0
