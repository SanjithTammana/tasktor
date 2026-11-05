using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Render;

/// <summary>
/// Resolves card values against the latest snapshot (U4 D-G1 step 1). A literal resolves to itself; a binding to the
/// value at its JSON Pointer. A missing pointer renders "—" and is reported once; it never throws.
/// </summary>
public sealed class BindingResolver
{
    public const string Missing = "—";

    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public BindingResolver(JsonNode? snapshotRoot) => Root = snapshotRoot;

    public JsonNode? Root { get; set; }

    /// <summary>Called once per missing pointer (viewers log it).</summary>
    public Action<string>? OnMissing { get; set; }

    public JsonNode? Resolve(Value? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Bind is { } pointer)
        {
            if (JsonPointer.TryResolve(Root, pointer, out var node))
            {
                return node;
            }

            if (_reported.Add(pointer))
            {
                OnMissing?.Invoke(pointer);
            }

            return null;
        }

        return value.Raw is null ? null : JsonNode.Parse(value.Raw);
    }

    public double? Number(Value? value) => AsDouble(Resolve(value));

    /// <summary>A number node as a double, whatever CLR type backs it (parsed JSON, or an int / long from a delta).</summary>
    public static double? AsDouble(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? (v.TryGetValue<double>(out var d) ? d : double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture))
            : null;

    /// <summary>Text for display: numbers rounded (≥ 10 → integer, else one decimal), strings as is, missing as "—".</summary>
    public string Text(Value? value) => Format(Resolve(value));

    public Severity SeverityOf(Value? value) =>
        Resolve(value) is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>() switch { "critical" => Severity.Critical, "warn" => Severity.Warn, _ => Severity.Normal }
            : Severity.Normal;

    public JsonArray? Array(Value? value) => Resolve(value) as JsonArray;

    public static string Format(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Missing;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                var d = AsDouble(v)!.Value;
                return Math.Abs(d) >= 10 ? Math.Round(d).ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.#", CultureInfo.InvariantCulture);
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return v.GetValue<string>();
            case JsonValue v when v.GetValueKind() == JsonValueKind.True:
                return "yes";
            case JsonValue v when v.GetValueKind() == JsonValueKind.False:
                return "no";
            case JsonValue v when v.GetValueKind() == JsonValueKind.Null:
                return Missing;
            default:
                return node.ToJsonString();
        }
    }
}
