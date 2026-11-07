"""Reference tokenizer and episode builder: mirrors Tasktor.Core.Workflow.Tokenizer / EpisodeBuilder exactly (C4)."""

SEPARATORS = [" - ", " — ", " – ", " | "]
KNOWN_SUFFIXES = {
    "google chrome", "microsoft edge", "mozilla firefox", "brave", "opera", "vivaldi", "visual studio code", "visual studio",
    "notepad", "file explorer", "word", "excel", "powerpoint", "microsoft word", "microsoft excel", "microsoft powerpoint",
    "windows terminal", "terminal", "obsidian", "notion", "slack", "discord", "spotify", "zoom", "outlook", "cursor",
}
STOPWORDS = {
    "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "new", "of", "on", "or", "tab",
    "the", "this", "that", "to", "untitled", "was", "with", "you", "your",
}

DEFAULTS = {"segment_min_ms": 2000, "glance_max_ms": 30000, "idle_split_ms": 300000, "activity_cap_ms": 3600000,
            "window_active_ms": 900000, "window_min_active_ms": 180000}


def strip_suffix(title, app):
    for sep in SEPARATORS:
        i = title.rfind(sep)
        if i <= 0:
            continue
        last = title[i + len(sep):].strip().lower()
        base = (app[:-4] if app and app.lower().endswith(".exe") else (app or "")).lower()
        if last in KNOWN_SUFFIXES or (len(base) >= 3 and base in last):
            return title[:i]
    return title


def _words(text):
    out, red = [], False
    for ch in text:
        if ch == "‹":
            red = True
            out.append(" ")
            continue
        if ch == "›":
            red = False
            continue
        if not red:
            out.append(ch.lower() if ch.isalnum() else " ")
    for w in "".join(out).split(" "):
        if 2 <= len(w) <= 30 and w not in STOPWORDS and not w.isdigit():
            yield w


def tokens(title, app, domain):
    out = list(_words(strip_suffix(title, app))) if title else []
    if domain:
        out.append("dom:" + domain.lower())
    return out


def _distinct(xs):
    return list(dict.fromkeys(xs))


class _Run:
    def __init__(self, index, app, start, prev, start_utc, tz):
        self.index, self.app, self.start, self.prev, self.start_utc, self.tz = index, app, start, prev, start_utc, tz
        self.active = self.idle = self.job = self.glances = self.title_changes = 0
        self.tokens = {}


class EpisodeBuilder:
    GAP_START = {"lock", "suspend", "agent_stop", "pause", "excluded_start"}
    GAP_END = {"unlock": "lock", "resume": "suspend", "agent_start": "agent_stop", "unpause": "pause", "excluded_end": "excluded_start"}

    def __init__(self, t=None, max_tokens=256):
        self.t = dict(DEFAULTS, **(t or {}))
        self.max_tokens = max_tokens
        self.cursor = None
        self.offset = 0
        self.tz = 0
        self.fg = None  # dict(app, win, title, tokens)
        self.gaps = set()
        self.idle_since = None
        self.job_on = False
        self.act = None
        self.visit = None
        self.last_app = None
        self.next_activity = 0
        self.win = None
        self.next_window = 0
        self.sess = None
        self.next_session = 0
        self.boundary = True
        self.activities, self.windows, self.sessions = [], [], []

    # ---- events ----
    def on(self, e):
        t = max(e["mono_ms"], self.cursor if self.cursor is not None else e["mono_ms"])
        self._accrue(t)
        self.offset = e["ts_utc_ms"] - e["mono_ms"]
        self.tz = e.get("tz_min", 0)
        k = e["kind"]
        if k == "fg" and e.get("app"):
            self._focus(e["app"], e.get("win"), e.get("title"), e.get("domain"))
        elif k == "title" and self.fg is not None and e.get("app") and (e.get("win") == self.fg["win"] or e["app"] == self.fg["app"]):
            self._set_title(e.get("title"), e.get("domain"))
        elif k == "idle_start" and e.get("idle") != "media":
            if self.idle_since is None:
                self.idle_since = t
        elif k == "idle_end":
            self._end_idle(t)
        elif k in self.GAP_START:
            self._start_gap(k, t)
        elif k in self.GAP_END:
            self.gaps.discard(self.GAP_END[k])
        elif k == "job_on":
            self.job_on = True
        elif k == "job_off":
            self.job_on = False

    def tick(self, mono):
        self._accrue(max(mono, self.cursor if self.cursor is not None else mono))

    def flush(self, mono):
        t = max(mono, self.cursor if self.cursor is not None else mono)
        self._accrue(t)
        self._close_visit_as_glance()
        self._close_activity(t, None)
        self._close_window(t)
        self._close_session(t)

    # ---- time ----
    def _accrue(self, t):
        if self.cursor is None:
            self.cursor = t
            return
        frm = self.cursor
        self.cursor = t
        if t <= frm or self.gaps or self.fg is None:
            self._check_idle(t)
            return
        if self.idle_since is not None:
            if t - self.idle_since >= self.t["idle_split_ms"]:
                self._split_at_idle(self.idle_since)
            return
        self._credit(frm, t)

    def _check_idle(self, t):
        if self.idle_since is not None and t - self.idle_since >= self.t["idle_split_ms"]:
            self._split_at_idle(self.idle_since)

    def _split_at_idle(self, idle_start):
        self._close_visit_as_glance()
        self._close_activity(idle_start, None)
        self._close_window(idle_start)
        self._close_session(idle_start)
        self.boundary = True

    def _end_idle(self, t):
        if self.idle_since is not None:
            if t - self.idle_since < self.t["idle_split_ms"] and self.act is not None:
                self.act.idle += t - self.idle_since
            self.idle_since = None

    def _window_room(self):
        return self.t["window_active_ms"] - (self.win["active"] if self.win else 0)

    def _credit(self, frm, to):
        at = frm
        while at < to:
            fg = self.fg
            if self.act is None:
                self._open_activity(fg["app"], at, None)
            if self.act.app == fg["app"] and self.visit is None:
                room = min(self.t["activity_cap_ms"] - self.act.active, self._window_room())
                step = min(to - at, room)
                self._add(self.act, fg, step)
                self._credit_window(self.act.index, step, at)
                at += step
                if self.act.active >= self.t["activity_cap_ms"]:
                    app = self.act.app
                    self._close_activity(at, None)
                    self._open_activity(app, at, app)
                if self.win is not None and self.win["active"] >= self.t["window_active_ms"]:
                    self._close_window(at)
                continue
            if self.visit is None:
                self.visit = _Run(-1, fg["app"], at, self.act.app, at + self.offset, self.tz)
            part = min(to - at, self.t["glance_max_ms"] - self.visit.active)
            self._add(self.visit, fg, part)
            at += part
            if self.visit.active >= self.t["glance_max_ms"]:
                self._promote()

    def _add(self, r, fg, ms):
        r.active += ms
        if self.job_on:
            r.job += ms
        for tok in fg["tokens"]:
            r.tokens[tok] = r.tokens.get(tok, 0) + ms

    def _credit_window(self, activity, ms, start):
        if ms <= 0:
            return
        if self.win is None:
            self.win = {"index": self.next_window, "start": start, "active": 0, "dwell": {}}
            self.next_window += 1
        self.win["active"] += ms
        self.win["dwell"][activity] = self.win["dwell"].get(activity, 0) + ms

    def _open_activity(self, app, start, prev):
        self.act = _Run(self.next_activity, app, start, prev if prev is not None else self.last_app, start + self.offset, self.tz)
        self.next_activity += 1
        if self.boundary or self.sess is None:
            self._close_session(start)
            self.sess = {"index": self.next_session, "start": start, "acts": []}
            self.next_session += 1
            self.boundary = False
        self.sess["acts"].append(self.act.index)

    def _absorb(self, a, remaining):
        while remaining > 0:
            step = min(remaining, self._window_room())
            self._credit_window(a.index, step, self.cursor - remaining)
            remaining -= step
            if self.win is not None and self.win["active"] >= self.t["window_active_ms"]:
                self._close_window(self.cursor - remaining)

    def _promote(self):
        v, self.visit = self.visit, None
        self._close_activity(v.start, v.app)
        self._open_activity(v.app, v.start, None)
        a = self.act
        a.active, a.job, a.title_changes = v.active, v.job, v.title_changes
        a.tokens.update(v.tokens)
        self._absorb(a, v.active)

    def _close_visit_as_glance(self):
        v, a = self.visit, self.act
        self.visit = None
        if v is None or a is None:
            return
        if v.active >= self.t["segment_min_ms"]:
            a.glances += 1
        a.active += v.active
        a.job += v.job
        self._absorb(a, v.active)

    def _focus(self, app, win, title, domain):
        if self.visit is not None and self.visit.app != app:
            self._close_visit_as_glance()
        self.fg = {"app": app, "win": win, "title": title, "tokens": _distinct(tokens(title, app, domain))}

    def _set_title(self, title, domain):
        fg = self.fg
        if fg["title"] == title:
            return
        fg["title"] = title
        fg["tokens"] = _distinct(tokens(title, fg["app"], domain))
        target = self.visit if self.visit is not None else (self.act if self.act is not None and self.act.app == fg["app"] else None)
        if target is not None:
            target.title_changes += 1

    def _start_gap(self, kind, t):
        self._close_visit_as_glance()
        self._close_activity(t, None)
        self._close_window(t)
        if kind in ("lock", "suspend", "agent_stop"):
            self._close_session(t)
            self.boundary = True
        self.gaps.add(kind)
        self.fg = None
        self.idle_since = None

    def _close_activity(self, end, nxt):
        a, self.act = self.act, None
        if a is None:
            return
        self.last_app = a.app
        if a.active <= 0:
            if self.sess is not None and a.index in self.sess["acts"]:
                self.sess["acts"].remove(a.index)
            return
        toks = sorted(a.tokens.items(), key=lambda kv: (-kv[1], kv[0]))[: self.max_tokens]
        self.activities.append({
            "index": a.index, "app": a.app, "start_ms": a.start, "end_ms": end, "start_utc_ms": a.start_utc, "tz_min": a.tz,
            "active_ms": a.active, "idle_ms": a.idle, "glances": a.glances, "title_changes": a.title_changes,
            "prev_app": a.prev, "next_app": nxt, "job_frac": round(a.job / a.active, 6) if a.active else 0.0,
            "tokens": [[k, v] for k, v in toks],
        })

    def _close_window(self, end):
        w, self.win = self.win, None
        if w is not None and w["active"] >= self.t["window_min_active_ms"]:
            self.windows.append({"index": w["index"], "start_ms": w["start"], "end_ms": end, "active_ms": w["active"],
                                 "dwell": [[k, v] for k, v in sorted(w["dwell"].items())]})

    def _close_session(self, end):
        s, self.sess = self.sess, None
        if s is not None and s["acts"]:
            self.sessions.append({"index": s["index"], "start_ms": s["start"], "end_ms": end, "activities": list(s["acts"])})


def build(events, end_ms, thresholds=None):
    b = EpisodeBuilder(thresholds)
    for e in events:
        b.on(e)
    b.flush(end_ms)
    return {"activities": b.activities, "windows": b.windows, "sessions": b.sessions}
