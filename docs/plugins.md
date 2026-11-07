# Writing a Tasktor plugin

A plugin is a folder with a `plugin.json` and a program. Tasktor runs the program on a schedule, gives it one JSON object
on stdin, and shows the cards it prints. Plugins are how Tasktor reaches anything outside your PC: the agent itself never
opens a network connection, so a plugin is the only place network use can live, and each one says where it goes.

Any language works. The examples use Python because Tasktor ships a Python runtime (`{python}` in `command`).

## Start in one minute

```
tasktor plugin new my-plugin
tasktor plugin check my-plugin --run
```

`plugin new` writes a small plugin that already works. `plugin check` runs the agent's own checks without installing
anything:

- it validates `plugin.json` with the same rules the agent uses;
- it prints exactly what the consent dialog will show;
- with `--run`, it runs the plugin once, as the agent would, against a throwaway data folder and a sample snapshot, and
  checks the output.

Options: `--set key=value` (repeatable) passes settings, and `--action <id>` sends one of your actions instead of a
render. `--run` executes your program with your rights, but without the agent's 256 MB job limit.

To install, copy the folder to `%LOCALAPPDATA%\Tasktor\plugins\<id>\` and turn it on in Settings → Plugins.

## `plugin.json`

Schema: [`schemas/tasktor.plugin-1.json`](../schemas/tasktor.plugin-1.json). Point `$schema` at it and your editor will
check the file as you type.

| Field | Rule |
|---|---|
| `contract` | `1` |
| `id` | lower-case letters, digits, `.`, `_`, `-`; **must equal the folder name** |
| `name`, `version`, `author`, `description` | shown in Settings and in the consent dialog |
| `command` | the program and its arguments; `{python}` is Tasktor's Python; a first part that is a file in the folder runs from there |
| `interval` | how often it runs: at least `30s`, or `"manual"` (only on Refresh or an action) |
| `timeout` | at most `60s`; a run past it is stopped and the card shows an error |
| `reads` | plain words for what it reads ("The file you choose") |
| `network` | every host it contacts; `[]` if none |
| `settings` | `{key, label, type}` with type `string`, `path`, `bool`, `number`, `choice` or `secret` (secrets are stored encrypted for this user) |
| `actions` | `{id, label, class}` with class `read` or `reversible`; plugins never get destructive actions |
| `min_tasktor` | the oldest Tasktor it works with |

## Input (stdin)

One JSON object, [`schemas/tasktor.plugin-input-1.json`](../schemas/tasktor.plugin-input-1.json):

```json
{ "contract": 1, "call": "render", "settings": { "label": "Runs" },
  "snapshot_path": "…\\snapshot.json", "data_dir": "…\\plugin-data\\my-plugin" }
```

- `call` is `render` on the schedule, and `action` (with `action` and `args`) when the user presses one of your buttons.
- `data_dir` is your own folder for state between runs. Nothing else of Tasktor's is yours to write.
- `snapshot_path` is Tasktor's public `snapshot.json` (`state` and `cards`), which you may read.
- The working directory is your plugin folder. `PYTHONPATH`, `PYTHONHOME`, `PYTHONSTARTUP`, `NODE_PATH` and
  `NODE_OPTIONS` are cleared.

## Output (stdout)

One JSON document, at most 64 KB, [`schemas/tasktor.plugin-output-1.json`](../schemas/tasktor.plugin-output-1.json):

```json
{ "contract": 1, "cards": [ { "id": "main", "title": "My plugin",
  "blocks": [ { "kind": "Metric", "priority": 1, "label": "Runs", "value": 3 } ],
  "actions": [ { "id": "reset", "label": "Reset count", "priority": 1 } ] } ] }
```

- At most 3 cards, each with at most 3 actions, and every action id declared in `plugin.json`.
- Blocks: `Metric`, `Badge`, `Bar`, `Gauge`, `Progress`, `Sparkline` (up to 60 points), `Table` (up to 12 rows), `List`,
  `Countdown`, `Actions`. `Tree` and `Alert` are not available to plugins: alerts and toasts go through rules.
- **Literal values only.** Plugins send numbers and text, never bindings into Tasktor's state.
- `priority` decides what stays when a card is shown small (1 is kept first).
- Exit with code 0. Anything on stderr is ignored, except that `plugin check` shows its last lines when a run fails.

## Consent, and keeping it honest

Turning a plugin on shows its name, author, `reads`, `network`, actions and the exact command line. Tasktor then pins the
SHA-256 of `plugin.json` and of the command's file. **Any change to either asks for consent again**, so an update cannot
quietly widen what a plugin does.

The dialog also says what Tasktor cannot enforce:
- `network` is a declaration: Tasktor cannot block a plugin's network use (that needs admin firewall rules);
- a plugin runs with the user's rights, like any program they run.

So the declarations are a promise to your users. Keep `reads` and `network` true and specific.

Plugins never get the event store, the capture vault, the search index, other plugins' data, Tasktor's settings, the
install key or any process action.

## Packs

A pack is a folder with a `pack.json` (`id`, `name`, `version`, `author`, `plugins`: the folder names inside it) and
optionally a `rules.pack.json`. Turning on any plugin of a pack shows one dialog listing every plugin in it. A pack's
rules all start off; the user ticks the ones they want.

## Publishing

Plugins live in their own repositories. To make yours easy to find:

- put the plugin folder at the root of the repository, or one folder per plugin;
- tag the repository with the GitHub topic `tasktor-plugin`;
- say in the README what it reads and contacts, matching `plugin.json`;
- run `tasktor plugin check . --run` in your CI.

Examples in this repository: [`examples/plugins/todo-count`](../examples/plugins/todo-count) and the first-party
plugins in [`plugins-builtin/`](../plugins-builtin).
