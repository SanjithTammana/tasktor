"""python -I -m tasktor_worker: one refit. Job JSON on stdin; progress JSON lines on stdout; exit 0 with a staged model."""
import json
import sys

from tasktor_worker.pipeline import run


def embed(args):
    """`embed --model DIR`: chunk or query texts (JSON lines on stdin) to int8 vectors (JSON lines on stdout)."""
    from tasktor_worker.embed import OnnxEncoder, embed_lines

    model = args[args.index("--model") + 1] if "--model" in args else None
    if model is None:
        sys.stderr.write("embed: no model\n")
        return 2
    try:
        embed_lines(OnnxEncoder.load(model), sys.stdin, sys.stdout)
    except Exception as e:  # no runtime, no model, a bad file: no vectors, never the texts
        sys.stderr.write("embed failed: %s\n" % type(e).__name__)
        return 1
    return 0


def gate8(args):
    """`g8 --model DIR --prereg FILE`: the check set ({"texts", "labels"}) on stdin; the gate's report on stdout."""
    from tasktor_worker.embed import OnnxEncoder, g8

    prereg = json.loads(open(args[args.index("--prereg") + 1], encoding="utf-8").read())
    check = json.load(sys.stdin)
    report = g8(check["texts"], check["labels"], OnnxEncoder.load(args[args.index("--model") + 1]), prereg["gates"]["g8_ari_gain"])
    sys.stdout.write(json.dumps(report) + "\n")
    return 0 if report["pass"] else 3


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "embed":
        return embed(sys.argv[2:])
    if len(sys.argv) > 1 and sys.argv[1] == "g8":
        return gate8(sys.argv[2:])
    job = json.load(sys.stdin)
    try:
        run(job)
    except Exception as e:  # the agent reads stderr into log/; the exit code tells B5 the refit failed
        sys.stderr.write("refit failed: %s\n" % type(e).__name__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
