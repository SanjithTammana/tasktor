import pathlib
import sys

WORKER = pathlib.Path(__file__).resolve().parents[1]
REPO = WORKER.parent
sys.path.insert(0, str(WORKER))
