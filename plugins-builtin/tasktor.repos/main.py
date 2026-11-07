"""tasktor.repos (E10 Repo hygiene): dirty, unpushed and behind repos under the folders you choose.

For each git repository found up to 3 levels down it runs `git status --porcelain=v2 --branch`: the `# branch.ab +N -M`
line gives ahead / behind against the upstream, and the entry count gives dirty files. It reads counts only, never file
contents, diffs or commit messages. `git fetch` runs only when "Check remotes" is on (the only network use, D-S2).
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time

MAX_DEPTH = 3
MAX_REPOS = 200
GIT_TIMEOUT_S = 15
# The whole scan stays inside the plugin's 60 s timeout (U1 D-H7); repos not reached are checked first next run.
BUDGET_S = 45
SKIP = {"node_modules", ".venv", "venv", "__pycache__", ".tox", "target", "build", "dist", ".cache"}


def find_repos(roots, max_depth=MAX_DEPTH):
    repos = []
    for root in roots:
        root = pathlib.Path(root).expanduser()
        if not root.is_dir():
            continue
        stack = [(root, 0)]
        while stack and len(repos) < MAX_REPOS:
            folder, depth = stack.pop()
            if (folder / ".git").exists():
                repos.append(folder)
                continue  # a repo's own subfolders are not searched (submodules are its business)
            if depth >= max_depth:
                continue
            try:
                children = sorted((p for p in folder.iterdir() if p.is_dir() and not p.is_symlink() and p.name not in SKIP), reverse=True)
            except OSError:
                continue
            stack.extend((c, depth + 1) for c in children)
    return repos


def parse_status(text):
    """-> dict(branch, upstream, ahead, behind, dirty) from `git status --porcelain=v2 --branch`."""
    info = {"branch": None, "upstream": None, "ahead": 0, "behind": 0, "dirty": 0}
    for line in text.splitlines():
        if line.startswith("# branch.head "):
            info["branch"] = line[len("# branch.head "):]
        elif line.startswith("# branch.upstream "):
            info["upstream"] = line[len("# branch.upstream "):]
        elif line.startswith("# branch.ab "):
            parts = line.split()
            info["ahead"] = abs(int(parts[2]))
            info["behind"] = abs(int(parts[3]))
        elif line and not line.startswith("#"):
            info["dirty"] += 1
    return info


def git(args, cwd, runner=subprocess.run):
    try:
        r = runner(["git", *args], cwd=str(cwd), capture_output=True, text=True, timeout=GIT_TIMEOUT_S,
                   env={**os.environ, "GIT_TERMINAL_PROMPT": "0", "GIT_OPTIONAL_LOCKS": "0"})
    except (subprocess.TimeoutExpired, OSError):
        return None  # a slow or broken repo is skipped this run, never fatal
    return r.stdout if r.returncode == 0 else None


def load_offset(data_dir):
    try:
        return int(json.loads((pathlib.Path(data_dir) / "scan.json").read_text())["next"])
    except (OSError, ValueError, KeyError, TypeError):
        return 0


def save_offset(data_dir, value):
    if data_dir:
        try:
            (pathlib.Path(data_dir) / "scan.json").write_text(json.dumps({"next": value}))
        except OSError:
            pass


def run(request, runner=subprocess.run, which=shutil.which, clock=time.monotonic):
    settings = request.get("settings", {})
    if request.get("call") == "action" and request.get("action") == "open":
        target = (request.get("args") or {}).get("repo")
        if target and os.path.isdir(target) and hasattr(os, "startfile"):
            os.startfile(target)  # noqa: S606 - opens the folder the user clicked, in Explorer
    folders = [f for f in (settings.get("folders") or "").replace(";", "\n").splitlines() if f.strip()]
    if not which("git"):
        return card([{"kind": "Badge", "priority": 1, "text": "Git is not on PATH"}], "warn")
    if not folders:
        return card([{"kind": "Badge", "priority": 1, "text": "Choose folders to look for repositories in Settings"}])
    fetch = str(settings.get("check_remotes", "false")).lower() in ("true", "1", "yes")
    rows = []
    repos = find_repos(folders)
    data_dir = request.get("data_dir")
    start = load_offset(data_dir) % len(repos) if repos and data_dir else 0
    order = repos[start:] + repos[:start]
    began = clock()
    checked = 0
    for repo in order:
        if clock() - began > BUDGET_S:
            break
        checked += 1
        if fetch:
            git(["fetch", "--quiet", "--no-tags"], repo, runner)
        out = git(["status", "--porcelain=v2", "--branch"], repo, runner)
        if out is None:
            continue
        s = parse_status(out)
        rows.append({"repo": repo.name, "path": str(repo), "dirty": s["dirty"], "unpushed": s["ahead"], "behind": s["behind"],
                     "upstream": s["upstream"] is not None})
    attention = [r for r in rows if r["dirty"] or r["unpushed"] or r["behind"]]
    attention.sort(key=lambda r: (-(r["dirty"] > 0), -r["unpushed"], -r["behind"], r["repo"]))
    blocks = [
        {"kind": "Metric", "priority": 1, "label": "Need attention", "value": len(attention)},
        {"kind": "Table", "priority": 2, "label": "Repositories", "max": 8, "row_key": "path",
         "columns": [{"key": "repo", "label": "Repo"}, {"key": "dirty", "label": "Dirty"},
                     {"key": "unpushed", "label": "Unpushed"}, {"key": "behind", "label": "Behind"}],
         "rows": [{k: r[k] for k in ("repo", "path", "dirty", "unpushed", "behind")} for r in attention]},
    ]
    save_offset(data_dir, (start + checked) % len(repos) if repos and checked < len(repos) else 0)
    if checked < len(repos):
        blocks.append({"kind": "Badge", "priority": 2, "text": f"Checked {checked} of {len(repos)} repositories in the time limit; the rest next run"})
    if not fetch:
        blocks.append({"kind": "Badge", "priority": 3, "text": "Behind counts are as of your last fetch"})
    out = card(blocks, "warn" if attention else "normal")
    out["cards"][0]["actions"] = [{"id": "refresh", "label": "Refresh", "priority": 1}]
    return out


def card(blocks, severity="normal"):
    return {"contract": 1, "cards": [{"id": "main", "title": "Repositories", "icon": "git", "severity": severity, "blocks": blocks}]}


def main():
    json.dump(run(json.load(sys.stdin)), sys.stdout)


if __name__ == "__main__":
    main()
