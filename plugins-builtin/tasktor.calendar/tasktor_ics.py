"""A small RFC 5545 reader for the subset Tasktor uses (U2 CalendarSource), standard library only.

The same subset as the agent's C# reader: VEVENT with DTSTART / DTEND / DURATION, RRULE (FREQ DAILY / WEEKLY / MONTHLY /
YEARLY, INTERVAL, COUNT, UNTIL, BYDAY, BYMONTH, BYMONTHDAY), EXDATE, RECURRENCE-ID overrides, STATUS:CANCELLED, and
VTIMEZONE. A TZID resolves through zoneinfo when it knows the id, else through the file's VTIMEZONE, else to local time.
Descriptions and attendees are never read (D-S5). Expansion happens in each event's own wall-clock time.
"""
import datetime as dt
import re

try:
    import zoneinfo
except ImportError:  # pragma: no cover
    zoneinfo = None

UTC = dt.timezone.utc
MAX_INSTANCES = 200
WINDOW = dt.timedelta(days=14)
_DAYS = {"MO": 0, "TU": 1, "WE": 2, "TH": 3, "FR": 4, "SA": 5, "SU": 6}


def unfold(text):
    out = []
    for raw in text.split("\n"):
        line = raw.rstrip("\r")
        if line[:1] in (" ", "\t") and out:
            out[-1] += line[1:]
        else:
            out.append(line)
    return [l for l in out if l]


def split_line(line):
    quoted = False
    for i, ch in enumerate(line):
        if ch == '"':
            quoted = not quoted
        elif ch == ":" and not quoted:
            head, value = line[:i], line[i + 1:]
            parts = head.split(";")
            params = {}
            for p in parts[1:]:
                if "=" in p:
                    k, v = p.split("=", 1)
                    params[k.strip().upper()] = v.strip().strip('"')
            return parts[0].strip().upper(), params, value
    return None


def unescape(s):
    return re.sub(r"\\(.)", lambda m: "\n" if m.group(1) in "nN" else m.group(1), s)


def parse_dt(s):
    """-> (naive datetime, is_utc, is_date) or None."""
    s = s.strip()
    utc = s.endswith("Z")
    core = s[:-1] if utc else s
    try:
        if len(core) == 8:
            return dt.datetime.strptime(core, "%Y%m%d"), utc, True
        return dt.datetime.strptime(core, "%Y%m%dT%H%M%S"), utc, False
    except ValueError:
        return None


def parse_duration(s):
    m = re.fullmatch(r"([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?", s.strip())
    if not m or not any(m.groups()[1:]):
        return None
    w, d, h, mi, se = (int(x or 0) for x in m.groups()[1:])
    td = dt.timedelta(weeks=w, days=d, hours=h, minutes=mi, seconds=se)
    return -td if m.group(1) == "-" else td


def parse_offset(s):
    m = re.fullmatch(r"([+-])(\d\d)(\d\d)(\d\d)?", (s or "").strip())
    if not m:
        return None
    td = dt.timedelta(hours=int(m.group(2)), minutes=int(m.group(3)))
    return -td if m.group(1) == "-" else td


def parse_rrule(text):
    rule = {"freq": None, "interval": 1, "count": None, "until": None, "byday": [], "bymonth": [], "bymonthday": []}
    for part in text.split(";"):
        if "=" not in part:
            continue
        k, v = part.split("=", 1)
        k = k.strip().upper()
        v = v.strip()
        if k == "FREQ":
            if v.upper() not in ("DAILY", "WEEKLY", "MONTHLY", "YEARLY"):
                return None
            rule["freq"] = v.upper()
        elif k == "INTERVAL" and v.isdigit() and 0 < int(v) < 1000:
            rule["interval"] = int(v)
        elif k == "COUNT" and v.isdigit() and int(v) > 0:
            rule["count"] = int(v)
        elif k == "UNTIL":
            p = parse_dt(v)
            if p:
                value, utc, is_date = p
                rule["until"] = (value + dt.timedelta(days=1) - dt.timedelta(microseconds=1) if is_date else value, utc)
        elif k == "BYDAY":
            for d in v.split(","):
                m = re.fullmatch(r"([+-]?\d{1,2})?(MO|TU|WE|TH|FR|SA|SU)", d.strip().upper())
                if m:
                    rule["byday"].append((int(m.group(1) or 0), _DAYS[m.group(2)]))
        elif k == "BYMONTH":
            rule["bymonth"] += [int(x) for x in v.split(",") if x.strip().lstrip("-").isdigit() and 1 <= int(x) <= 12]
        elif k == "BYMONTHDAY":
            rule["bymonthday"] += [int(x) for x in v.split(",") if re.fullmatch(r"-?\d+", x.strip()) and 1 <= abs(int(x)) <= 31]
    return rule if rule["freq"] else None


def _add_months(d, n):
    m = d.month - 1 + n
    return d.replace(year=d.year + m // 12, month=m % 12 + 1, day=1)


def _days_in_month(d):
    return (_add_months(d, 1) - dt.timedelta(days=1)).day


def _in_span(start, end, byday):
    for ordinal, wd in byday:
        days = []
        d = start + dt.timedelta(days=(wd - start.weekday()) % 7)
        while d < end:
            days.append(d)
            d += dt.timedelta(days=7)
        if ordinal == 0:
            yield from days
        else:
            i = ordinal - 1 if ordinal > 0 else len(days) + ordinal
            if 0 <= i < len(days):
                yield days[i]


def _month_days(first, monthdays):
    n = _days_in_month(first)
    for md in monthdays:
        day = md if md > 0 else n + md + 1
        if 1 <= day <= n:
            yield first.replace(day=day)


def _in_month(first, start, r):
    if r["byday"]:
        days = list(_in_span(first, _add_months(first, 1), r["byday"]))
        if r["bymonthday"]:
            allowed = set(_month_days(first, r["bymonthday"]))
            days = [d for d in days if d in allowed]
        return days
    if r["bymonthday"]:
        return list(_month_days(first, r["bymonthday"]))
    return [first.replace(day=start.day)] if start.day <= _days_in_month(first) else []


def _candidates(period, start, r):
    f = r["freq"]
    month_ok = lambda d: not r["bymonth"] or d.month in r["bymonth"]
    if f == "DAILY":
        if month_ok(period) and (not r["byday"] or any(wd == period.weekday() for _, wd in r["byday"])) \
                and (not r["bymonthday"] or period in set(_month_days(period.replace(day=1), r["bymonthday"]))):
            return [period]
        return []
    if f == "WEEKLY":
        wds = [wd for _, wd in r["byday"]] or [start.weekday()]
        return [period + dt.timedelta(days=wd) for wd in wds if month_ok(period + dt.timedelta(days=wd))]
    if f == "MONTHLY":
        return _in_month(period, start, r) if month_ok(period) else []
    if not r["bymonth"] and r["byday"] and not r["bymonthday"]:
        return list(_in_span(period, period.replace(year=period.year + 1), r["byday"]))
    out = []
    for m in r["bymonth"] or [start.month]:
        out += _in_month(period.replace(month=m), start, r)
    return out


def expand(start, rule, max_periods=100_000):
    """Yield wall-clock datetimes: DTSTART first (RFC 5545), then the rule's instances in order; COUNT includes DTSTART."""
    yield start
    emitted = 1
    if rule["count"] and emitted >= rule["count"]:
        return
    base = start.replace(hour=0, minute=0, second=0, microsecond=0)
    for p in range(max_periods):
        step = p * rule["interval"]
        try:
            if rule["freq"] == "DAILY":
                period = base + dt.timedelta(days=step)
            elif rule["freq"] == "WEEKLY":
                period = base - dt.timedelta(days=base.weekday()) + dt.timedelta(days=7 * step)
            elif rule["freq"] == "MONTHLY":
                period = _add_months(base.replace(day=1), step)
            else:
                period = base.replace(year=base.year + step, month=1, day=1)
        except (OverflowError, ValueError):
            return
        if period.year > 9000:
            return
        for day in sorted(set(_candidates(period, start, rule))):
            t = day.replace(hour=start.hour, minute=start.minute, second=start.second)
            if t <= start:
                continue
            yield t
            emitted += 1
            if rule["count"] and emitted >= rule["count"]:
                return


class Zones:
    def __init__(self, file_zones, local):
        self.file = file_zones
        self.local = local
        self._cache = {}

    def _system(self, tzid):
        if tzid not in self._cache:
            try:
                self._cache[tzid] = zoneinfo.ZoneInfo(tzid) if zoneinfo else None
            except Exception:  # unknown key, bad path, no tz database
                self._cache[tzid] = None
        return self._cache[tzid]

    def at(self, wall, tzid):
        """The aware datetime of wall-clock time `wall` in `tzid` (None = local)."""
        if tzid is None:
            return wall.replace(tzinfo=self.local)
        tz = self._system(tzid)
        if tz is not None:
            return wall.replace(tzinfo=tz)
        if tzid in self.file:
            return wall.replace(tzinfo=dt.timezone(file_offset(self.file[tzid], wall)))
        return wall.replace(tzinfo=self.local)


def file_offset(observances, wall):
    best = None
    offset = min(observances, key=lambda o: o["start"])["from"]
    for ob in observances:
        onsets = [ob["start"]] if not ob["rule"] else []
        if ob["rule"]:
            for t in expand(ob["start"], ob["rule"]):
                if t > wall:
                    break
                onsets.append(t)
        for onset in onsets + ob["rdates"]:
            if onset <= wall and (best is None or onset > best):
                best, offset = onset, ob["to"]
    return offset


def parse(text):
    """-> (events, zones, skipped). Events are dicts; zones map TZID -> list of observances."""
    events, zones, skipped = [], {}, 0
    stack, props, observances, zone_id = [], None, [], None
    for line in unfold(text):
        sp = split_line(line)
        if not sp:
            skipped += 1
            continue
        name, params, value = sp
        if name == "BEGIN":
            stack.append(value.upper())
            if value.upper() in ("VEVENT", "STANDARD", "DAYLIGHT"):
                props = {}
            elif value.upper() == "VTIMEZONE":
                observances, zone_id, props = [], None, {}
            continue
        if name == "END":
            kind = stack.pop() if stack else ""
            if kind == "VEVENT" and props is not None:
                ev = _event(props)
                if ev:
                    events.append(ev)
                else:
                    skipped += 1
                props = None
            elif kind in ("STANDARD", "DAYLIGHT") and props is not None:
                ob = _observance(props)
                if ob:
                    observances.append(ob)
                props = {} if stack and stack[-1] == "VTIMEZONE" else None
            elif kind == "VTIMEZONE":
                if zone_id and observances:
                    zones[zone_id] = observances
                props = None
            continue
        if stack and stack[-1] == "VTIMEZONE" and name == "TZID":
            zone_id = value
            continue
        if props is not None:
            props.setdefault(name, []).append((params, value))
    return events, zones, skipped


def _time(prop):
    params, value = prop
    p = parse_dt(value)
    if not p:
        return None
    wall, utc, is_date = p
    return {"wall": wall, "utc": utc, "tzid": None if utc else params.get("TZID"), "date": is_date or params.get("VALUE", "").upper() == "DATE"}


def _first(props, name):
    v = props.get(name)
    return v[0][1] if v else None


def _event(props):
    if "DTSTART" not in props:
        return None
    start = _time(props["DTSTART"][0])
    if not start:
        return None
    exdates = []
    for params, value in props.get("EXDATE", []):
        for part in value.split(","):
            t = _time((params, part))
            if t:
                exdates.append(t)
    rr = _first(props, "RRULE")
    return {
        "uid": _first(props, "UID") or "noid-%s" % start["wall"].isoformat(),
        "start": start,
        "end": _time(props["DTEND"][0]) if "DTEND" in props else None,
        "duration": parse_duration(_first(props, "DURATION")) if _first(props, "DURATION") else None,
        "summary": unescape(_first(props, "SUMMARY") or ""),
        "location": unescape(_first(props, "LOCATION")) if _first(props, "LOCATION") else None,
        "url": _first(props, "URL"),
        "rule": parse_rrule(rr) if rr else None,
        "exdates": exdates,
        "recurrence_id": _time(props["RECURRENCE-ID"][0]) if "RECURRENCE-ID" in props else None,
        "cancelled": (_first(props, "STATUS") or "").upper() == "CANCELLED",
    }


def _observance(props):
    start = parse_dt(_first(props, "DTSTART") or "")
    frm, to = parse_offset(_first(props, "TZOFFSETFROM")), parse_offset(_first(props, "TZOFFSETTO"))
    if not start or frm is None or to is None:
        return None
    rr = _first(props, "RRULE")
    rdates = []
    for _, v in props.get("RDATE", []):
        for part in v.split(","):
            p = parse_dt(part)
            if p:
                rdates.append(p[0])
    return {"start": start[0], "from": frm, "to": to, "rule": parse_rrule(rr) if rr else None, "rdates": rdates}


def _resolve(t, zones):
    if t["utc"]:
        return t["wall"].replace(tzinfo=UTC)
    return zones.at(t["wall"].replace(hour=0, minute=0, second=0) if t["date"] else t["wall"], None if t["date"] else t["tzid"])


def _length(ev, zones):
    if ev["end"]:
        d = _resolve(ev["end"], zones) - _resolve(ev["start"], zones)
        return d if d > dt.timedelta(0) else dt.timedelta(0)
    if ev["duration"] and ev["duration"] > dt.timedelta(0):
        return ev["duration"]
    return dt.timedelta(days=1) if ev["start"]["date"] else dt.timedelta(0)


def _row(ev, start, length, calendar):
    row = {"uid": ev["uid"], "start": start.astimezone(UTC), "title": ev["summary"], "all_day": ev["start"]["date"]}
    if calendar:
        row["calendar"] = calendar
    if length > dt.timedelta(0):
        row["end"] = (start + length).astimezone(UTC)
    if ev["location"]:
        row["location"] = ev["location"]
    if ev["url"]:
        row["url"] = ev["url"]
    return row


def expand_calendar(text, frm, to, local=UTC, calendar=None):
    """Expanded instances in [frm, to) (ongoing ones kept), as tasktor.calendar/1 event dicts, earliest MAX_INSTANCES."""
    events, file_zones, _ = parse(text)
    zones = Zones(file_zones, local)
    rows = []
    overrides = [e for e in events if e["recurrence_id"]]
    overridden = {}
    for o in overrides:
        overridden.setdefault(o["uid"], set()).add(_resolve(o["recurrence_id"], zones).astimezone(UTC))

    for ev in events:
        if ev["recurrence_id"] or ev["cancelled"]:
            continue
        length = _length(ev, zones)
        tzid = None if ev["start"]["date"] or ev["start"]["utc"] else ev["start"]["tzid"]
        exdates = {_resolve(x, zones).astimezone(UTC) for x in ev["exdates"]}
        exdays = {x["wall"].date() for x in ev["exdates"] if x["date"]}
        skip = overridden.get(ev["uid"], set())
        until = None
        if ev["rule"] and ev["rule"]["until"]:
            uwall, uutc = ev["rule"]["until"]
            until = uwall.replace(tzinfo=UTC) if uutc else zones.at(uwall, tzid)
        wall0 = ev["start"]["wall"]
        walls = expand(wall0, ev["rule"]) if ev["rule"] else [wall0]
        for n, wall in enumerate(walls):
            if n > 50_000:
                break
            start = wall.replace(tzinfo=UTC) if ev["start"]["utc"] else zones.at(wall, tzid)
            if start >= to or (until is not None and start > until):
                break
            su = start.astimezone(UTC)
            if su in exdates or (ev["start"]["date"] and wall.date() in exdays) or su in skip:
                continue
            if start + length > frm or (length == dt.timedelta(0) and start >= frm):
                rows.append(_row(ev, start, length, calendar))

    for o in overrides:
        if o["cancelled"]:
            continue
        start = _resolve(o["start"], zones)
        length = _length(o, zones)
        if start < to and (start + length > frm or (length == dt.timedelta(0) and start >= frm)):
            rows.append(_row(o, start, length, calendar))

    rows.sort(key=lambda r: r["start"])
    return rows[:MAX_INSTANCES]
