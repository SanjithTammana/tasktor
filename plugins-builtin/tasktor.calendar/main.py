"""tasktor.calendar: fetches the ICS calendars you add and writes tasktor.calendar/1 for the agent (U2 D-S2).

The agent never connects to the network; this plugin does, only to the URLs in its settings, with conditional requests
(If-None-Match / If-Modified-Since) so an unchanged calendar costs one small response. It expands the next 14 days and
writes plugin-data/tasktor.calendar/events.json atomically. URLs are secrets: they are never printed, logged or put in the
file; a feed is named by its host only.
"""
import datetime as dt
import json
import os
import pathlib
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tasktor_ics  # noqa: E402

MAX_BYTES = 8 * 1024 * 1024
TIMEOUT_S = 20


def feed_urls(settings):
    raw = settings.get("urls", "") or ""
    urls = []
    for token in raw.replace(",", "\n").split():
        token = token.strip()
        if token.lower().startswith("webcal://"):
            token = "https://" + token[len("webcal://"):]
        urls.append(token)
    return urls


def fetch(url, cache, opener=urllib.request.urlopen):
    """-> (text or None, error or None). Uses and updates `cache` (etag / last_modified / body path)."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https":
        return None, "use an https:// or webcal:// address"
    req = urllib.request.Request(url, headers={"User-Agent": "tasktor-calendar/1"})
    if cache.get("etag"):
        req.add_header("If-None-Match", cache["etag"])
    if cache.get("last_modified"):
        req.add_header("If-Modified-Since", cache["last_modified"])
    try:
        with opener(req, timeout=TIMEOUT_S) as resp:
            body = resp.read(MAX_BYTES + 1)
            if len(body) > MAX_BYTES:
                return None, "calendar too big"
            cache["etag"] = resp.headers.get("ETag")
            cache["last_modified"] = resp.headers.get("Last-Modified")
            text = body.decode("utf-8", errors="replace")
            if "BEGIN:VCALENDAR" not in text[:4096].upper():
                return None, "not a calendar"
            return text, None
    except urllib.error.HTTPError as e:
        if e.code == 304 and cache.get("body"):
            return cache["body"], None
        return None, "HTTP %d" % e.code
    except (urllib.error.URLError, TimeoutError, OSError):
        return None, "could not connect"


def run(request, now=None, opener=urllib.request.urlopen):
    data_dir = pathlib.Path(request.get("data_dir") or ".")
    data_dir.mkdir(parents=True, exist_ok=True)
    cache_path = data_dir / "cache.json"
    try:
        caches = json.loads(cache_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        caches = {}
    now = now or dt.datetime.now(dt.timezone.utc)
    local = dt.datetime.now().astimezone().tzinfo
    calendars, events, new_caches = [], [], {}
    for i, url in enumerate(feed_urls(request.get("settings", {}))):
        cid = "feed-%d" % (i + 1)
        host = urllib.parse.urlsplit(url).hostname or cid
        cache = dict(caches.get(cid, {}))
        body_path = data_dir / (cid + ".ics")
        if body_path.exists():
            cache["body"] = body_path.read_text(encoding="utf-8")
        text, error = fetch(url, cache, opener)
        if text is None and error and cache.get("body") and error == "could not connect":
            text, error = cache["body"], None  # offline: keep showing the last good copy
        if text is not None:
            if text != cache.get("body"):
                tmp = body_path.with_suffix(".ics.tmp")
                tmp.write_text(text, encoding="utf-8")
                os.replace(tmp, body_path)
            events += tasktor_ics.expand_calendar(text, now, now + tasktor_ics.WINDOW, local, host)
            calendars.append({"id": cid, "name": host, "status": "ok"})
        else:
            calendars.append({"id": cid, "name": host, "status": "error", "error": error})
        new_caches[cid] = {k: cache.get(k) for k in ("etag", "last_modified") if cache.get(k)}

    events.sort(key=lambda e: e["start"])
    doc = {
        "schema": "tasktor.calendar/1",
        "generated_at": iso(now),
        "calendars": calendars,
        "events": [{k: iso(v) if isinstance(v, dt.datetime) else v for k, v in e.items()} for e in events[: tasktor_ics.MAX_INSTANCES]],
    }
    write_json(data_dir / "events.json", doc)
    write_json(cache_path, new_caches)
    ok = sum(1 for c in calendars if c["status"] == "ok")
    bad = len(calendars) - ok
    text = "Add a calendar address in Settings" if not calendars else "calendars: %d ok%s" % (ok, ", %d error" % bad if bad else "")
    card = {"id": "main", "title": "Calendars", "icon": "calendar", "severity": "warn" if bad else "normal",
            "blocks": [{"kind": "Badge", "priority": 1, "text": text},
                       {"kind": "Metric", "priority": 2, "label": "Events, next 14 days", "value": len(doc["events"])}]}
    return {"contract": 1, "cards": [card]}


def iso(t):
    return t.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_json(path, doc):
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(doc), encoding="utf-8")
    os.replace(tmp, path)


def main():
    json.dump(run(json.load(sys.stdin)), sys.stdout)


if __name__ == "__main__":
    main()
