using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tasktor.Core.Features.Agents;

/// <summary>A proposed change to an agent's settings file: the new text and a line diff to show before writing.</summary>
public sealed record HookChange(string NewText, IReadOnlyList<string> Diff, bool AlreadyInstalled);

/// <summary>
/// I1 Q1 hook setup for Claude Code: Tasktor proposes the exact change to the agent's <c>settings.json</c> and writes it
/// only on confirm, keeping a <c>.bak</c>; Remove takes out exactly the entries it added (commands starting with
/// <c>tasktor agent-hook</c>) and nothing else. It never edits a settings file silently. Pure text in, text out; the action
/// handler does the file I/O.
/// </summary>
public static class HookInstaller
{
    public const string Marker = "tasktor agent-hook";

    /// <summary>Claude Code hook events → the state each reports (I1).</summary>
    public static readonly (string Event, string? Matcher, string State)[] ClaudeHooks =
    [
        ("UserPromptSubmit", null, "working"),
        ("Notification", "permission_prompt", "needs-permission"),
        ("Notification", "idle_prompt", "waiting"),
        ("Stop", null, "waiting"),
        ("SessionEnd", null, "ended"),
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, TypeInfoResolver = JsonNodeContext.Default };

    public static string Command(string state, string agent) => Marker + " " + state + " --agent " + agent;

    public static HookChange Install(string? currentText, string agent = "claude")
    {
        var root = Parse(currentText);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var already = true;
        foreach (var (evt, matcher, state) in ClaudeHooks)
        {
            var list = hooks[evt] as JsonArray ?? new JsonArray();
            var cmd = Command(state, agent);
            if (list.OfType<JsonObject>().Any(g => (g["hooks"] as JsonArray)?.OfType<JsonObject>().Any(h => (string?)h["command"] == cmd) == true))
            {
                continue;
            }

            already = false;
            var group = new JsonObject();
            if (matcher is not null)
            {
                group["matcher"] = matcher;
            }

            group["hooks"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "command", ["command"] = cmd });
            list.Add((JsonNode)group);
            hooks[evt] = list;
        }

        root["hooks"] = hooks;
        return Change(currentText, root, already);
    }

    public static HookChange Remove(string? currentText)
    {
        var root = Parse(currentText);
        var found = false;
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var evt in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[evt] is not JsonArray list)
                {
                    continue;
                }

                foreach (var group in list.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is JsonArray inner)
                    {
                        foreach (var h in inner.OfType<JsonObject>().Where(h => ((string?)h["command"])?.StartsWith(Marker, StringComparison.Ordinal) == true).ToList())
                        {
                            inner.Remove(h);
                            found = true;
                        }

                        if (inner.Count == 0)
                        {
                            list.Remove(group);
                        }
                    }
                }

                if (list.Count == 0)
                {
                    hooks.Remove(evt);
                }
            }

            if (hooks.Count == 0)
            {
                root.Remove("hooks");
            }
        }

        return Change(currentText, root, !found);
    }

    private static JsonObject Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("The settings file is not a JSON object.");
    }

    private static HookChange Change(string? before, JsonObject root, bool unchanged)
    {
        var after = root.ToJsonString(Indented) + Environment.NewLine;
        var a = (before ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var b = after.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        return new HookChange(after, unchanged ? [] : LineDiff(string.IsNullOrEmpty(before) ? [] : a, b), unchanged);
    }

    /// <summary>A minimal LCS line diff: "  " same, "- " removed, "+ " added. Settings files are small.</summary>
    public static List<string> LineDiff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var diff = new List<string>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                diff.Add("  " + a[x++]);
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                diff.Add("- " + a[x++]);
            }
            else
            {
                diff.Add("+ " + b[y++]);
            }
        }

        diff.AddRange(a.Skip(x).Select(l => "- " + l));
        diff.AddRange(b.Skip(y).Select(l => "+ " + l));
        return diff;
    }

    /// <summary>The changed lines only, with a little context, for the confirm dialog.</summary>
    public static string Summary(IReadOnlyList<string> diff, int context = 1)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var keep = new bool[diff.Count];
        for (var i = 0; i < diff.Count; i++)
        {
            if (!diff[i].StartsWith("  ", StringComparison.Ordinal))
            {
                for (var k = Math.Max(0, i - context); k <= Math.Min(diff.Count - 1, i + context); k++)
                {
                    keep[k] = true;
                }
            }
        }

        var sb = new StringBuilder();
        for (var i = 0; i < diff.Count; i++)
        {
            if (keep[i])
            {
                sb.AppendLine(diff[i]);
            }
            else if (i > 0 && keep[i - 1])
            {
                sb.AppendLine("  …");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonNode))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class JsonNodeContext : System.Text.Json.Serialization.JsonSerializerContext;
