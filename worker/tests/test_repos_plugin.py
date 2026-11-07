"""tasktor.repos (E10): finds repos to depth 3, counts dirty / unpushed / behind, fetches only when asked."""
import importlib.util
import json
import pathlib
import subprocess

import jsonschema

ROOT = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("tasktor_repos_main", ROOT / "plugins-builtin" / "tasktor.repos" / "main.py")
plugin = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(plugin)
OUTPUT = json.loads((ROOT / "schemas" / "tasktor.plugin-output-1.json").read_text())


def sh(*args, cwd):
    subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True,
                   env={"GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.invalid", "GIT_COMMITTER_NAME": "t",
                        "GIT_COMMITTER_EMAIL": "t@example.invalid", "HOME": str(cwd), "PATH": "/usr/bin:/bin"})


def make_repos(tmp):
    origin = tmp / "origin.git"
    sh("init", "--bare", "-q", str(origin), cwd=tmp)
    work = tmp / "code" / "group" / "app"
    work.mkdir(parents=True)
    sh("init", "-q", "-b", "main", cwd=work)
    (work / "a.txt").write_text("1")
    sh("add", "a.txt", cwd=work)
    sh("commit", "-qm", "one", cwd=work)
    sh("remote", "add", "origin", str(origin), cwd=work)
    sh("push", "-qu", "origin", "main", cwd=work)
    (work / "a.txt").write_text("2")
    sh("commit", "-qam", "two", cwd=work)
    (work / "b.txt").write_text("new")
    clean = tmp / "code" / "clean"
    clean.mkdir()
    sh("init", "-q", cwd=clean)
    deep = tmp / "code" / "a" / "b" / "c" / "too-deep"
    deep.mkdir(parents=True)
    sh("init", "-q", cwd=deep)
    return tmp / "code"


def test_parse_status_counts():
    s = plugin.parse_status("# branch.oid abc\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -3\n1 .M N... 100644 100644 100644 a b a.txt\n? b.txt\n")
    assert (s["ahead"], s["behind"], s["dirty"], s["upstream"]) == (2, 3, 2, "origin/main")


def test_finds_repos_to_depth_3_and_reports_only_ones_needing_attention(tmp_path):
    code = make_repos(tmp_path)
    found = sorted(p.name for p in plugin.find_repos([code]))
    assert found == ["app", "clean"]
    out = plugin.run({"contract": 1, "call": "render", "settings": {"folders": str(code)}})
    jsonschema.Draft202012Validator(OUTPUT).validate(out)
    table = out["cards"][0]["blocks"][1]
    assert [(r["repo"], r["dirty"], r["unpushed"], r["behind"]) for r in table["rows"]] == [("app", 1, 1, 0)]
    assert out["cards"][0]["blocks"][0]["value"] == 1


def test_fetch_runs_only_when_check_remotes_is_on(tmp_path):
    code = make_repos(tmp_path)
    calls = []

    def runner(argv, **kw):
        calls.append(argv[1])
        return subprocess.run(argv, **kw)

    plugin.run({"call": "render", "settings": {"folders": str(code)}}, runner=runner)
    assert "fetch" not in calls
    plugin.run({"call": "render", "settings": {"folders": str(code), "check_remotes": "true"}}, runner=runner)
    assert "fetch" in calls


def test_without_git_the_card_says_so():
    out = plugin.run({"call": "render", "settings": {"folders": "/tmp"}}, which=lambda _: None)
    assert out["cards"][0]["blocks"][0]["text"] == "Git is not on PATH"


def test_manifest_validates():
    schema = json.loads((ROOT / "schemas" / "tasktor.plugin-1.json").read_text())
    jsonschema.Draft202012Validator(schema).validate(json.loads((ROOT / "plugins-builtin" / "tasktor.repos" / "plugin.json").read_text()))


def test_a_git_call_that_times_out_is_skipped_not_fatal(tmp_path):
    code = make_repos(tmp_path)

    def slow(argv, **kw):
        raise subprocess.TimeoutExpired(argv, kw.get("timeout"))

    out = plugin.run({"call": "render", "settings": {"folders": str(code)}}, runner=slow)
    jsonschema.Draft202012Validator(OUTPUT).validate(out)
    assert out["cards"][0]["blocks"][0]["value"] == 0


def test_the_scan_stops_at_its_budget_and_starts_next_run_where_it_stopped(tmp_path):
    code = make_repos(tmp_path)
    data = tmp_path / "data"
    data.mkdir()
    ticks = iter([0, 0, plugin.BUDGET_S + 1, plugin.BUDGET_S + 1, plugin.BUDGET_S + 1])
    seen = []

    def runner(argv, **kw):
        seen.append(kw["cwd"])
        return subprocess.run(argv, **kw)

    request = {"call": "render", "settings": {"folders": str(code)}, "data_dir": str(data)}
    out = plugin.run(request, runner=runner, clock=lambda: next(ticks))
    badges = [b["text"] for b in out["cards"][0]["blocks"] if b["kind"] == "Badge"]
    assert "Checked 1 of 2 repositories in the time limit; the rest next run" in badges
    first = seen[0]
    seen.clear()
    plugin.run(request, runner=runner, clock=lambda: 0)
    assert seen[0] != first  # the next run starts with the one it did not reach
