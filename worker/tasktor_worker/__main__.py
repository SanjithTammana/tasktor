"""python -I -m tasktor_worker: one refit. Job JSON on stdin; progress JSON lines on stdout; exit 0 with a staged model."""
import json
import sys

from tasktor_worker.pipeline import run


def main():
    job = json.load(sys.stdin)
    try:
        run(job)
    except Exception as e:  # the agent reads stderr into log/; the exit code tells B5 the refit failed
        sys.stderr.write("refit failed: %s\n" % type(e).__name__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
