using System.Text;
using System.Text.Json;

namespace Tasktor.Core.Rules;

/// <summary>
/// U4 D-G3 Settings → Rules: a rule's switch writes <c>{ "id": "…", "enabled": true|false }</c> into the user's
/// <c>rules.json</c> (D-H6's toggle form, read by <see cref="RulesLoader"/>). The file is the user's, with comments: it is
/// edited as text, never re-serialised. An earlier toggle for the rule is replaced in place; otherwise one is added at the
/// end of the list, after any rule of the user's with that id, so it switches that one. Text that is not valid JSON is
/// refused (<see cref="FormatException"/>) rather than overwritten.
/// </summary>
public static class RulesToggle
{
    private static readonly JsonReaderOptions Reader = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonDocumentOptions Doc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A new user rules.json: a comment pointing at the shipped rules and the grammar, and an empty list.</summary>
    public const string Starter = "// Your Tasktor rules. The shipped ones are in rules.default.json in the install folder; a rule here with the\n"
        + "// same id replaces one of them, and { \"id\": \"…\", \"enabled\": false } turns one off. Settings' switches add those lines.\n"
        + "{\n  \"rules\": [\n  ],\n}\n";

    public static string Apply(string? text, string id, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(id);
        var toggle = "{ \"id\": \"" + JsonEncodedText.Encode(id) + "\", \"enabled\": " + (enabled ? "true" : "false") + " }";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "// Your rules (see rules.default.json for the shipped ones). Tasktor's Settings switches add the toggles below.\n{\n  \"rules\": [\n    " + toggle + ",\n  ],\n}\n";
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        int rootStart = -1, arrayStart = -1, lastEnd = -1;
        (int Start, int End)? lastToggle = null;
        var ruleAfterToggle = false;
        try
        {
            var reader = new Utf8JsonReader(bytes, Reader);
            while (reader.Read())
            {
                if (reader.CurrentDepth == 0 && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new FormatException("rules.json is not an object");
                    }

                    rootStart = (int)reader.TokenStartIndex;
                    continue;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                var isRules = reader.ValueTextEquals("rules"u8);
                reader.Read();
                if (!isRules || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }

                arrayStart = (int)reader.TokenStartIndex;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var start = (int)reader.TokenStartIndex;
                    var isObject = reader.TokenType == JsonTokenType.StartObject;
                    reader.Skip();
                    var end = (int)reader.BytesConsumed;
                    lastEnd = end;
                    if (!isObject)
                    {
                        continue;
                    }

                    using var el = JsonDocument.Parse(bytes.AsMemory(start, end - start), Doc);
                    var root = el.RootElement;
                    if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String || idProp.GetString() != id)
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("when", out _) && root.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        lastToggle = (start, end);
                        ruleAfterToggle = false;
                    }
                    else
                    {
                        ruleAfterToggle = true; // the user's own rule with this id: a toggle must come after it
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new FormatException("rules.json is not valid JSON; fix it before using the switches", ex);
        }

        if (rootStart < 0)
        {
            throw new FormatException("rules.json is not an object");
        }

        string Splice(int at, int removeTo, string insert) =>
            Encoding.UTF8.GetString(bytes, 0, at) + insert + Encoding.UTF8.GetString(bytes, removeTo, bytes.Length - removeTo);

        if (lastToggle is { } t && !ruleAfterToggle)
        {
            return Splice(t.Start, t.End, toggle); // switch the earlier toggle in place
        }

        if (arrayStart < 0)
        {
            return Splice(rootStart + 1, rootStart + 1, "\n  \"rules\": [\n    " + toggle + ",\n  ],"); // a trailing comma is fine here
        }

        return lastEnd < 0
            ? Splice(arrayStart + 1, arrayStart + 1, "\n    " + toggle + ",\n  ")
            : Splice(lastEnd, lastEnd, ",\n    " + toggle); // after the last entry (a trailing comma after it stays valid)
    }
}
