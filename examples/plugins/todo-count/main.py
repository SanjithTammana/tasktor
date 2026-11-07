"""Example Tasktor plugin: counts open TODO lines in one text file.

Tasktor runs this with the plugin folder as the working directory and one JSON object on stdin:
{contract, call: "render" | "action", action?, args?, settings, snapshot_path, data_dir}.
It prints one JSON document (<= 64 KB): up to 3 cards made of the plugin block subset, literal values only.
"""
import json
import pathlib
import sys


def open_items(path):
    items = []
    for line in pathlib.Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        text = line.strip()
        if text.startswith(("- [ ]", "* [ ]")):
            items.append(text[5:].strip())
        elif text.upper().startswith("TODO"):
            items.append(text[4:].lstrip(":- ").strip())
    return items


def main():
    request = json.load(sys.stdin)
    file = request.get("settings", {}).get("file")
    if not file:
        card = {"id": "main", "title": "TODOs", "icon": "list",
                "blocks": [{"kind": "Badge", "priority": 1, "text": "Choose a file in Settings"}]}
    else:
        items = open_items(file)
        card = {
            "id": "main",
            "title": "TODOs",
            "icon": "list",
            "severity": "warn" if len(items) > 20 else "normal",
            "blocks": [
                {"kind": "Metric", "priority": 1, "label": "Open", "value": len(items)},
                {"kind": "List", "priority": 2, "items": items[:5], "max": 5},
            ],
            "actions": [{"id": "refresh", "label": "Refresh", "priority": 1}],
        }
    json.dump({"contract": 1, "cards": [card]}, sys.stdout)


if __name__ == "__main__":
    main()
