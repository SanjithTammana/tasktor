"""Tasktor progress helper (B4.7): stdlib only, copy it next to your script.

    from tasktor_progress import Progress
    p = Progress("embed-run", total=5665, unit="docs", heartbeat_s=30)
    for i, doc in enumerate(docs, 1):
        embed(doc)
        p.update(i, msg=f"batch {i}")
    p.done()

It writes ``%LOCALAPPDATA%\\Tasktor\\jobs\\<id>.json`` (``tasktor.job/1``) atomically (temp file + rename), at most about once a
second, and, when the terminal supports it (Windows Terminal sets WT_SESSION), prints ``ESC ] 9 ; 4 ; state ; pct BEL`` so the
tab and the taskbar show the same progress. ``update()`` also honours Tasktor's polite pause: while
``jobs\\<id>.control.json`` says ``{"pause": true}`` it blocks there and marks the file ``"paused": true``.
"""
import datetime
import json
import os
import pathlib
import re
import sys
import time

_STATES = {"running": 1, "indeterminate": 3, "warning": 4, "error": 2, "done": 0}


def jobs_dir():
    base = os.environ.get("TASKTOR_DATA_DIR")
    if not base:
        local = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), ".local", "share")
        base = os.path.join(local, "Tasktor")
    return pathlib.Path(base) / "jobs"


class Progress:
    def __init__(self, job_id, total=None, unit=None, label=None, heartbeat_s=None, log=None, folder=None, osc=None, min_interval=1.0, clock=time.monotonic, sleep=time.sleep):
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,64}", job_id) or not job_id.strip("."):
            raise ValueError("job id must be [A-Za-z0-9._-]{1,64}")
        self.id = job_id
        self.total = total
        self.unit = unit
        self.label = label
        self.heartbeat_s = heartbeat_s
        self.log = log
        self.folder = pathlib.Path(folder) if folder else jobs_dir()
        self.folder.mkdir(parents=True, exist_ok=True)
        self.osc = bool(os.environ.get("WT_SESSION")) and sys.stdout.isatty() if osc is None else osc
        self.min_interval = min_interval
        self._clock = clock
        self._sleep = sleep
        self._last_write = None
        self._state = {"done": 0, "stage": None, "msg": None, "state": "running", "pct": None}
        self.writes = 0
        self._write(force=True)

    @property
    def path(self):
        return self.folder / f"{self.id}.json"

    @property
    def control_path(self):
        return self.folder / f"{self.id}.control.json"

    def update(self, done=None, msg=None, stage=None, pct=None, state="running"):
        if done is not None:
            self._state["done"] = done
        if msg is not None:
            self._state["msg"] = msg
        if stage is not None:
            self._state["stage"] = stage
        if pct is not None:
            self._state["pct"] = max(0.0, min(100.0, float(pct)))
        self._state["state"] = state
        self._write(force=False)
        self._wait_while_paused()

    def done(self, msg=None):
        self._state["state"] = "done"
        if self.total:
            self._state["done"] = self.total
        if msg:
            self._state["msg"] = msg
        self._write(force=True)

    def fail(self, msg=None):
        self._state["state"] = "error"
        if msg:
            self._state["msg"] = msg
        self._write(force=True)

    def _paused(self):
        try:
            return bool(json.loads(self.control_path.read_text(encoding="utf-8")).get("pause"))
        except (OSError, ValueError):
            return False

    def _wait_while_paused(self):
        if not self._paused():
            return
        self._write(force=True, paused=True)
        while self._paused():
            self._sleep(1.0)
        self._write(force=True, paused=False)

    def _document(self, paused):
        doc = {"schema": 1, "id": self.id, "pid": os.getpid(), "state": self._state["state"],
               "updated": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
        if self.total:
            doc["done"] = self._state["done"]
            doc["total"] = self.total
        elif self._state["pct"] is not None:
            doc["pct"] = self._state["pct"]
        for key in ("unit", "label", "log"):
            if getattr(self, key):
                doc[key] = getattr(self, key)
        if self.heartbeat_s:
            doc["heartbeat_s"] = int(self.heartbeat_s)
        for key in ("stage", "msg"):
            if self._state[key]:
                doc[key] = str(self._state[key])[:120]
        if paused:
            doc["paused"] = True
        return doc

    def _write(self, force, paused=False):
        now = self._clock()
        if not force and self._last_write is not None and now - self._last_write < self.min_interval:
            return  # faster writes are debounced, not rejected
        self._last_write = now
        tmp = self.path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(self._document(paused)), encoding="utf-8")
        os.replace(tmp, self.path)
        self.writes += 1
        if self.osc:
            pct = self._pct()
            sys.stdout.write(f"\x1b]9;4;{_STATES.get(self._state['state'], 1)};{0 if pct is None else int(pct)}\x07")
            sys.stdout.flush()

    def _pct(self):
        if self.total:
            return 100.0 * self._state["done"] / self.total
        return self._state["pct"]
