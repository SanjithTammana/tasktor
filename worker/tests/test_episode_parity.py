"""C4 acceptance 1: the Python reference builds the same segments, activities, windows and sessions as C# on every fixture."""
import json
import pathlib

import pytest

from tasktor_reference import episodes

DIR = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "episodes"
STREAMS = json.loads((DIR / "streams.json").read_text())
EXPECTED = json.loads((DIR / "expected.json").read_text())


def events(stream, base):
    out, jump, tz = [], 0, 0
    for e in stream["events"]:
        jump += e.get("utc_jump", 0)
        tz = e.get("tz", tz)
        out.append({"kind": e["k"], "mono_ms": e["t"], "ts_utc_ms": base + e["t"] + jump, "tz_min": tz, "app": e.get("app"),
                    "win": e.get("win"), "title": e.get("title"), "domain": e.get("domain"), "idle": e.get("idle")})
    return out


@pytest.mark.parametrize("stream", STREAMS["streams"], ids=lambda s: s["name"])
def test_reference_matches_csharp(stream):
    got = episodes.build(events(stream, STREAMS["base_utc_ms"]), stream["end"])
    assert got == EXPECTED[stream["name"]]


def test_tokenizer_matches_the_spec():
    assert episodes.tokens("Pull request 12 review - Google Chrome", "chrome.exe", "GitHub.com") == ["pull", "request", "review", "dom:github.com"]
    assert episodes.tokens("Invoice ‹num› for ‹email›", "x.exe", None) == ["invoice"]
