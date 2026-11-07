"""tasktor.calendar (U2 D-S2): parity with the agent's reader, conditional fetch, secrets never leak, valid output."""
import datetime as dt
import email.message
import io
import json
import pathlib
import sys
import urllib.error

import jsonschema
import pytest

ROOT = pathlib.Path(__file__).resolve().parents[2]
import importlib.util  # noqa: E402

sys.path.insert(0, str(ROOT / "plugins-builtin" / "tasktor.calendar"))
import tasktor_ics  # noqa: E402

_spec = importlib.util.spec_from_file_location("tasktor_calendar_main", ROOT / "plugins-builtin" / "tasktor.calendar" / "main.py")
plugin = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(plugin)

FIXTURE = ROOT / "tests" / "fixtures" / "ics"
SCHEMA = json.loads((ROOT / "schemas" / "tasktor.calendar-1.json").read_text())
SECRET = "https://calendar.example.com/private-abc123secret/basic.ics"


def test_expansion_matches_the_shared_fixture():
    exp = json.loads((FIXTURE / "sample.expected.json").read_text())
    frm = dt.datetime.fromisoformat(exp["from"].replace("Z", "+00:00"))
    rows = tasktor_ics.expand_calendar((FIXTURE / "sample.ics").read_text(), frm, frm + dt.timedelta(days=exp["days"]))
    assert [[r["uid"], r["start"].strftime("%Y-%m-%dT%H:%M:%SZ")] for r in rows] == exp["instances"]


def test_rrule_subset_and_unsupported_frequencies():
    assert tasktor_ics.parse_rrule("FREQ=HOURLY") is None
    r = tasktor_ics.parse_rrule("FREQ=MONTHLY;BYDAY=-1FR;COUNT=3")
    starts = list(tasktor_ics.expand(dt.datetime(2026, 1, 30, 10), r))
    assert [s.day for s in starts] == [30, 27, 27]
    assert tasktor_ics.parse_duration("P1DT2H30M") == dt.timedelta(days=1, hours=2, minutes=30)


class FakeResponse(io.BytesIO):
    def __init__(self, body, headers=None):
        super().__init__(body)
        self.headers = email.message.Message()
        for k, v in (headers or {}).items():
            self.headers[k] = v

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def make_opener(responses, seen):
    def opener(req, timeout):
        seen.append(dict(req.header_items()))
        r = responses.pop(0)
        if isinstance(r, Exception):
            raise r
        return r
    return opener


def run(tmp_path, responses, seen, urls=SECRET):
    now = dt.datetime(2026, 11, 2, tzinfo=dt.timezone.utc)
    req = {"contract": 1, "call": "render", "settings": {"urls": urls}, "snapshot_path": "", "data_dir": str(tmp_path)}
    return plugin.run(req, now=now, opener=make_opener(responses, seen))


def test_fetch_writes_a_valid_events_file_and_never_the_url(tmp_path):
    body = (FIXTURE / "sample.ics").read_bytes()
    seen = []
    out = run(tmp_path, [FakeResponse(body, {"ETag": '"v1"'})], seen)
    doc = json.loads((tmp_path / "events.json").read_text())
    jsonschema.Draft202012Validator(SCHEMA).validate(doc)
    assert len(doc["events"]) == 10
    assert doc["calendars"] == [{"id": "feed-1", "name": "calendar.example.com", "status": "ok"}]
    everything = json.dumps(out) + json.dumps(doc) + (tmp_path / "cache.json").read_text()
    assert "secret" not in everything and "private" not in everything
    assert out["cards"][0]["blocks"][0]["text"] == "calendars: 1 ok"


def test_a_second_run_is_conditional_and_304_reuses_the_saved_copy(tmp_path):
    body = (FIXTURE / "sample.ics").read_bytes()
    seen = []
    run(tmp_path, [FakeResponse(body, {"ETag": '"v1"', "Last-Modified": "Mon, 02 Nov 2026 00:00:00 GMT"})], seen)
    not_modified = urllib.error.HTTPError(SECRET, 304, "Not Modified", email.message.Message(), None)
    run(tmp_path, [not_modified], seen)
    assert seen[1].get("If-none-match") == '"v1"'
    assert seen[1].get("If-modified-since") == "Mon, 02 Nov 2026 00:00:00 GMT"
    assert len(json.loads((tmp_path / "events.json").read_text())["events"]) == 10


def test_errors_are_named_without_the_url_and_offline_keeps_the_last_copy(tmp_path):
    seen = []
    out = run(tmp_path, [urllib.error.HTTPError(SECRET, 404, "x", email.message.Message(), None),
                         FakeResponse(b"<html>login</html>")], seen,
              urls=SECRET + "\nwebcal://other.example.org/cal.ics\nhttp://plain.example.net/x.ics")
    cals = json.loads((tmp_path / "events.json").read_text())["calendars"]
    assert [(c["status"], c.get("error")) for c in cals] == [("error", "HTTP 404"), ("error", "not a calendar"), ("error", "use an https:// or webcal:// address")]
    assert out["cards"][0]["severity"] == "warn"

    body = (FIXTURE / "sample.ics").read_bytes()
    run(tmp_path, [FakeResponse(body)], seen)
    run(tmp_path, [urllib.error.URLError("down")], seen)
    assert len(json.loads((tmp_path / "events.json").read_text())["events"]) == 10


def test_no_urls_asks_for_one(tmp_path):
    out = run(tmp_path, [], [], urls="")
    assert out["cards"][0]["blocks"][0]["text"] == "Add a calendar address in Settings"


def test_manifest_validates():
    schema = json.loads((ROOT / "schemas" / "tasktor.plugin-1.json").read_text())
    jsonschema.Draft202012Validator(schema).validate(json.loads((ROOT / "plugins-builtin" / "tasktor.calendar" / "plugin.json").read_text()))
