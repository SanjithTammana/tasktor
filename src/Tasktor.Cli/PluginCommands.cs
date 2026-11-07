using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Plugins;
using Tasktor.Core.Plugins;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Cli;

/// <summary>
/// <c>tasktor plugin new|check</c>: tools for plugin authors that need neither the agent nor an install. <c>check</c> applies
/// the agent's own manifest and output checks (<see cref="PluginCheck"/>) and prints what the consent dialog will show;
/// with <c>--run</c> it runs the plugin once, as the agent would, against a throwaway data folder.
/// </summary>
public static partial class CliApp
{
    /// <summary>The newest plugin contract this Tasktor understands (checked against <c>min_tasktor</c>).</summary>
    private static readonly Version TasktorVersion = new(1, 0);

    private static async Task<int> Plugin(Args a, CliIo io) => a.Positional(0) switch
    {
        "new" => PluginNew(a, io),
        "check" => await PluginCheckAsync(a, io).ConfigureAwait(false),
        _ => Bad(io, "plugin: new or check"),
    };

    private static int PluginNew(Args a, CliIo io)
    {
        var id = a.Positional(1) ?? throw new ArgumentException("plugin new: which id? (lowercase letters, digits, - and .)");
        if (!PluginValidation.ValidId(id))
        {
            return Bad(io, "plugin new: '" + id + "' is not a plugin id (lowercase letters, digits, '.', '_' and '-', up to 64 characters, starting with a letter or digit)");
        }

        var folder = Path.Combine(a.Opt("--to") ?? Directory.GetCurrentDirectory(), id);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return Bad(io, "plugin new: " + folder + " exists and is not empty");
        }

        Directory.CreateDirectory(folder);
        foreach (var (name, text) in PluginTemplate.Files(id))
        {
            File.WriteAllText(Path.Combine(folder, name), text);
        }

        io.Out.WriteLine("Created " + folder);
        io.Out.WriteLine("Next: tasktor plugin check \"" + folder + "\" --run");
        return ExitCode.Ok;
    }

    private static async Task<int> PluginCheckAsync(Args a, CliIo io)
    {
        var folder = a.Positional(1) ?? throw new ArgumentException("plugin check: which folder?");
        var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "python", OperatingSystem.IsWindows() ? "python.exe" : "bin/python3");
        var python = File.Exists(bundled) ? bundled : PluginCheck.PathPython;
        var result = PluginCheck.Manifest(folder, TasktorVersion, python);
        if (result.Manifest is null || result.Problem is not null)
        {
            var why = result.Problem ?? "unreadable";
            io.Err.WriteLine(why.StartsWith("plugin.json", StringComparison.Ordinal) || why.StartsWith("no plugin.json", StringComparison.Ordinal) ? why : "plugin.json: " + why);
            io.Err.WriteLine("The agent would list it as an error and never run it.");
            return ExitCode.Error;
        }

        io.Out.WriteLine("plugin.json: ok");
        io.Out.WriteLine("The consent dialog will show:");
        foreach (var line in result.Consent)
        {
            io.Out.WriteLine("  " + line);
        }

        if (!a.Flag("--run"))
        {
            io.Out.WriteLine("Add --run to run it once and check its output.");
            return ExitCode.Ok;
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in a.Many("--set"))
        {
            var eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return Bad(io, "plugin check: --set key=value");
            }

            settings[kv[..eq]] = kv[(eq + 1)..];
        }

        var m = result.Manifest;
        var action = a.Opt("--action");
        if (action is not null && !(m.Actions ?? []).Any(x => x.Id == action))
        {
            return Bad(io, "plugin check: '" + action + "' is not one of the plugin's declared actions");
        }

        // A throwaway data folder and a sample snapshot: the check never touches the real store.
        var scratch = Path.Combine(Path.GetTempPath(), "tasktor-plugin-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(scratch, "data"));
        try
        {
            var snapshot = Path.Combine(scratch, "snapshot.json");
            await File.WriteAllTextAsync(snapshot, """{ "schema": "tasktor.snapshot/1", "state": {}, "cards": [] }""").ConfigureAwait(false);
            var input = PluginCheck.Input(action is null ? PluginCall.Render : PluginCall.Action, settings, snapshot, Path.Combine(scratch, "data"), action);
            var command = PluginCheck.ResolveCommand(m, folder, python);
            var timeout = Core.Time.DurationText.Parse(m.Timeout);
            var (exit, stdout, stderr, timedOut) = await RunOnce(command, folder, JsonSerializer.Serialize(input, ContractsJsonContext.Default.PluginInput), timeout).ConfigureAwait(false);
            if (timedOut)
            {
                io.Err.WriteLine("It ran past its timeout (" + m.Timeout + "); the agent would stop it and show an error.");
                return ExitCode.Error;
            }

            if (exit != 0)
            {
                io.Err.WriteLine("It exited with code " + exit + "; the agent would show an error." + (stderr.Length > 0 ? "\n" + Tail(stderr) : string.Empty));
                return ExitCode.Error;
            }

            var (cards, error) = PluginCheck.Output(m, stdout, io.Clock.UtcNow);
            if (cards is null)
            {
                io.Err.WriteLine("Output refused: " + error);
                return ExitCode.Error;
            }

            io.Out.WriteLine("Output: ok, " + cards.Count + (cards.Count == 1 ? " card" : " cards"));
            var resolver = new BindingResolver(new JsonObject());
            foreach (var card in cards)
            {
                io.Out.WriteLine("  " + CardFitter.Glance(card, resolver));
            }

            return ExitCode.Ok;
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder left behind is harmless
            }
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr, bool TimedOut)> RunOnce(List<string> command, string dir, string stdin, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(command[0]) { WorkingDirectory = dir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in command.Skip(1))
        {
            info.ArgumentList.Add(arg);
        }

        using var p = Process.Start(info) ?? throw new InvalidOperationException("could not start " + command[0]);
        await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return (-1, string.Empty, string.Empty, true);
        }

        return (p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false), false);
    }

    private static string Tail(string text)
    {
        var lines = text.TrimEnd().Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 10)));
    }
}

/// <summary>The files <c>tasktor plugin new</c> writes: a small plugin that already passes <c>plugin check --run</c>.</summary>
internal static class PluginTemplate
{
    public static IEnumerable<(string Name, string Text)> Files(string id)
    {
        yield return ("plugin.json", $$"""
            {
              "$schema": "https://raw.githubusercontent.com/SanjithTammana/tasktor/main/schemas/tasktor.plugin-1.json",
              "contract": 1,
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "0.1.0",
              "author": "",
              "description": "What it shows, in one line.",
              "command": ["{python}", "main.py"],
              "interval": "5m",
              "timeout": "10s",
              "reads": ["Nothing yet: list what it reads"],
              "network": [],
              "settings": [ { "key": "label", "label": "Label", "type": "string" } ],
              "actions": [ { "id": "reset", "label": "Reset count", "class": "reversible" } ],
              "min_tasktor": "1.0"
            }

            """);
        yield return ("main.py", """"
            """Tasktor plugin. Tasktor runs this with its folder as the working directory and one JSON object on stdin:
            {contract, call: "render" | "action", action?, args?, settings, snapshot_path, data_dir}.
            Print one JSON document (<= 64 KB): up to 3 cards, literal values only. See docs/plugins.md.
            """
            import json
            import pathlib
            import sys


            def main():
                request = json.load(sys.stdin)
                state_file = pathlib.Path(request["data_dir"]) / "state.json"  # your own folder; keep what you need here
                state = json.loads(state_file.read_text()) if state_file.exists() else {"runs": 0}
                if request["call"] == "action" and request.get("action") == "reset":
                    state["runs"] = 0
                else:
                    state["runs"] += 1
                state_file.write_text(json.dumps(state))

                label = request["settings"].get("label") or "Runs"
                card = {
                    "id": "main",
                    "title": "My plugin",
                    "blocks": [{"kind": "Metric", "priority": 1, "label": label, "value": state["runs"]}],
                    "actions": [{"id": "reset", "label": "Reset count", "priority": 1}],
                }
                json.dump({"contract": 1, "cards": [card]}, sys.stdout)


            if __name__ == "__main__":
                main()

            """");
        yield return ("README.md", $$"""
            # {{id}}

            A Tasktor plugin. Check it without installing anything:

                tasktor plugin check . --run

            Install: copy this folder to `%LOCALAPPDATA%\Tasktor\plugins\{{id}}\`, then turn it on in Tasktor → Settings → Plugins.
            The consent dialog shows what `plugin.json` says it reads and contacts, and the exact command line; keep those true.

            """);
    }
}
